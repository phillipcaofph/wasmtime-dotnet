using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Wasmtime.Components;

/// <summary>
/// The per-Store table of host objects that guests own or borrow. Wasmtime only sees a 32-bit
/// <em>rep</em> per object; this table maps reps back to objects.
/// </summary>
/// <remarks>
/// <para>
/// A rep is <c>index | generation &lt;&lt; 24</c> with a generation from 1 to 255, so rep 0 is never
/// issued and a stale rep from a freed slot does not resolve. Lookups also check the slot's type, so
/// a binding bug surfaces as an exception (a trap) rather than a wrong-type cast.
/// </para>
/// <para>
/// One managed object has at most one guest owner process-wide: a per-table reverse map rejects a
/// second <c>own</c> transfer into the same Store, and a process-wide weak table rejects a transfer
/// while a guest of another live Store owns the object. Locks are taken table first, then the
/// process-wide owner box.
/// </para>
/// </remarks>
internal sealed class HostResourceTable
{
    private const int GenerationShift = 24;
    private const uint IndexMask = (1u << GenerationShift) - 1;

    private static readonly ConditionalWeakTable<object, GuestOwnerBox> owners = new();

    private readonly object gate = new();
    private readonly List<Slot> slots = new();
    private readonly Stack<int> free = new();
    private readonly Dictionary<object, uint> owned = new(ResourceMarshalling.ReferenceComparer.Instance);
    private readonly Dictionary<object, uint> lent = new(ResourceMarshalling.ReferenceComparer.Instance);
    private readonly Store store;
    private int live;
    private int guestOwned;
    private int lentCount;
    private bool dead;

    public HostResourceTable(Store store) => this.store = store;

    private enum SlotState : byte
    {
        Free,
        Reserved,
        GuestOwned,
        Lent,
    }

    private struct Slot
    {
        public object? Value;
        public ComponentHostResourceType? Type;
        public SlotState State;
        public int Lends;
        public byte Generation;
    }

    /// <summary>The process-wide record of which table's guest owns an object, and how many tables lend it.</summary>
    private sealed class GuestOwnerBox
    {
        public WeakReference<HostResourceTable>? Owner;

        /// <summary>Lent slots for the object across all tables; while non-zero it cannot be moved.</summary>
        public int Lends;

        public bool HasLiveOwner(out HostResourceTable? owner)
        {
            owner = null;
            return Owner is { } reference && reference.TryGetTarget(out owner) && owner.IsAlive;
        }
    }

    /// <summary>Whether a guest of any live Store owns <paramref name="value"/>, as far as this binding knows.</summary>
    public static bool IsOwnedByAnyGuest(object value)
    {
        if (!owners.TryGetValue(value, out var box))
        {
            return false;
        }

        lock (box)
        {
            return box.HasLiveOwner(out _);
        }
    }

    /// <summary>
    /// Reserves a rep for moving <paramref name="value"/> into a guest. The reservation becomes
    /// guest ownership with <see cref="Commit"/>, or is undone with <see cref="Unreserve"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A guest of this or another live Store owns the object, or the Store's capacity is exhausted.
    /// </exception>
    public uint Reserve(ComponentHostResourceType type, object value)
    {
        if (!TryReserve(type, value, out var rep))
        {
            throw Released();
        }

        return rep;
    }

    /// <summary>
    /// Like <see cref="Reserve"/>, but returns false instead of throwing when the Store has released its
    /// component resources (<see cref="Drain"/>), so the caller knows the object was not taken.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A guest of this or another live Store owns the object, or the Store's capacity is exhausted.
    /// </exception>
    public bool TryReserve(ComponentHostResourceType type, object value, out uint rep)
    {
        lock (gate)
        {
            if (dead)
            {
                rep = 0;
                return false;
            }

            if (owned.ContainsKey(value))
            {
                throw new InvalidOperationException(
                    $"The {type.DisplayName} object is already owned by a component in this store; " +
                    "an object can only have one guest owner.");
            }

            ThrowIfFull(type);
            var box = owners.GetValue(value, _ => new GuestOwnerBox());
            lock (box)
            {
                if (box.HasLiveOwner(out var other) && !ReferenceEquals(other, this))
                {
                    throw new InvalidOperationException(
                        $"The {type.DisplayName} object is owned by a component in another store; " +
                        "an object can only have one guest owner.");
                }

                if (box.Lends > 0)
                {
                    throw new InvalidOperationException(
                        $"The {type.DisplayName} object is lent to a component call, so it cannot be moved into a component until the call ends.");
                }

                // Allocate first: when it throws, the object must not stay recorded as owned here.
                rep = Allocate(type, value, SlotState.Reserved);
                box.Owner = new WeakReference<HostResourceTable>(this);
            }

            owned.Add(value, rep);
            guestOwned++;
            return true;
        }
    }

    /// <summary>Turns a reservation into guest ownership. A slot freed meanwhile (the guest already
    /// dropped or returned the object during the call) is left alone.</summary>
    public void Commit(uint rep)
    {
        lock (gate)
        {
            if (TryFind(rep, out var index) && slots[index].State == SlotState.Reserved)
            {
                var slot = slots[index];
                slot.State = SlotState.GuestOwned;
                slots[index] = slot;
            }
        }
    }

    /// <summary>Undoes a reservation that was never handed to a guest and returns the object.</summary>
    public object? Unreserve(uint rep)
    {
        lock (gate)
        {
            if (!TryFind(rep, out var index) || slots[index].State != SlotState.Reserved)
            {
                return null;
            }

            return RemoveOwned(index);
        }
    }

    /// <summary>Lends <paramref name="value"/> to a guest for one call; returns the borrow's rep.</summary>
    public uint Lend(ComponentHostResourceType type, object value)
    {
        lock (gate)
        {
            ThrowIfDead();
            if (lent.TryGetValue(value, out var existing) && TryFind(existing, out var index) &&
                ReferenceEquals(slots[index].Type, type))
            {
                var slot = slots[index];
                slot.Lends++;
                slots[index] = slot;
                return existing;
            }

            ThrowIfFull(type);
            var box = owners.GetValue(value, _ => new GuestOwnerBox());
            uint rep;
            lock (box)
            {
                // A guest that owns the object could drop it, running its destructor, while it is lent; unless its
                // Store is running an operation further up this thread's stack (a callback passing on a borrow).
                if (box.HasLiveOwner(out var owner) && !owner!.store.IsInComponentOperationOnCurrentThread)
                {
                    throw new InvalidOperationException(
                        $"The {type.DisplayName} object is owned by a component, so the host cannot lend it.");
                }

                rep = Allocate(type, value, SlotState.Lent);
                box.Lends++;
            }

            lent[value] = rep;
            lentCount++;
            return rep;
        }
    }

    /// <summary>Ends one lend started by <see cref="Lend"/>.</summary>
    public void EndLend(uint rep)
    {
        lock (gate)
        {
            if (!TryFind(rep, out var index) || slots[index].State != SlotState.Lent)
            {
                return;
            }

            var slot = slots[index];
            if (--slot.Lends > 0)
            {
                slots[index] = slot;
                return;
            }

            // The map may hold the rep of the same object lent under another type; leave that one.
            if (lent.TryGetValue(slot.Value!, out var current) && current == rep)
            {
                lent.Remove(slot.Value!);
            }

            lentCount--;
            EndProcessLend(slot.Value!);
            Release(index);
        }
    }

    /// <summary>Gets the object behind a rep a guest lent or owns, for a borrow it passed in.</summary>
    /// <exception cref="InvalidOperationException">The rep is unknown or of another type.</exception>
    public object Get(ComponentHostResourceType type, uint rep)
    {
        lock (gate)
        {
            return slots[Find(type, rep)].Value!;
        }
    }

    /// <summary>
    /// Removes guest ownership of the object behind <paramref name="rep"/> and returns it, without
    /// running its destructor: the host owns it again.
    /// </summary>
    /// <exception cref="InvalidOperationException">The rep is unknown, of another type, or not owned.</exception>
    public object TakeBack(ComponentHostResourceType type, uint rep)
    {
        lock (gate)
        {
            var index = Find(type, rep);
            if (slots[index].State == SlotState.Lent)
            {
                throw new InvalidOperationException(
                    $"An owned {type.DisplayName} handle refers to an object the host only lent.");
            }

            return RemoveOwned(index);
        }
    }

    /// <summary>
    /// Removes the object a guest dropped and returns it; the caller runs the destructor outside the lock.
    /// </summary>
    public object ReleaseFromGuest(ComponentHostResourceType type, uint rep) => TakeBack(type, rep);

    public bool IsHeldByGuest(ComponentHostResourceType type, object value)
    {
        lock (gate)
        {
            return owned.TryGetValue(value, out var rep) && TryFind(rep, out var index) &&
                ReferenceEquals(slots[index].Type, type);
        }
    }

    public int CountHeldByGuest(ComponentHostResourceType type)
    {
        lock (gate)
        {
            var count = 0;
            foreach (var rep in owned.Values)
            {
                if (TryFind(rep, out var index) && ReferenceEquals(slots[index].Type, type))
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Objects guests own or have reserved.</summary>
    public int GuestOwnedCount
    {
        get
        {
            lock (gate)
            {
                return guestOwned;
            }
        }
    }

    /// <summary>Objects currently lent to guests.</summary>
    public int LentCount
    {
        get
        {
            lock (gate)
            {
                return lentCount;
            }
        }
    }

    /// <summary>
    /// Ends the table: every guest-owned or reserved object is returned for destruction (which the
    /// caller runs outside the lock), lends are forgotten, and later transfers are refused.
    /// </summary>
    public List<KeyValuePair<ComponentHostResourceType, object>> Drain()
    {
        lock (gate)
        {
            var result = new List<KeyValuePair<ComponentHostResourceType, object>>();
            for (var i = 0; i < slots.Count; i++)
            {
                var slot = slots[i];
                if (slot.State is SlotState.Reserved or SlotState.GuestOwned)
                {
                    result.Add(new KeyValuePair<ComponentHostResourceType, object>(slot.Type!, slot.Value!));
                }
            }

            Clear();
            return result;
        }
    }

    /// <summary>Forgets every object without running destructors (the Store was disposed).</summary>
    public void Abandon()
    {
        lock (gate)
        {
            Clear();
        }
    }

    // Read without the lock: Reserve checks another table while holding its own lock and an owner box.
    private bool IsAlive => !Volatile.Read(ref dead);

    private void Clear()
    {
        foreach (var value in owned.Keys)
        {
            ForgetOwner(value);
        }

        foreach (var slot in slots)
        {
            if (slot.State == SlotState.Lent)
            {
                EndProcessLend(slot.Value!);
            }
        }

        Volatile.Write(ref dead, true);
        owned.Clear();
        lent.Clear();
        slots.Clear();
        free.Clear();
        live = 0;
        guestOwned = 0;
        lentCount = 0;
    }

    private void ThrowIfDead()
    {
        if (dead)
        {
            throw Released();
        }
    }

    private static InvalidOperationException Released() =>
        new("The store has released its component resources, so no further component operations are possible.");

    private void ThrowIfFull(ComponentHostResourceType type)
    {
        if (live >= store.MaxComponentHostResources)
        {
            throw new InvalidOperationException(
                $"Transferring the {type.DisplayName} object would exceed Store.MaxComponentHostResources " +
                $"({store.MaxComponentHostResources}).");
        }
    }

    private uint Allocate(ComponentHostResourceType type, object value, SlotState state)
    {
        int index;
        Slot slot;
        if (free.Count > 0)
        {
            index = free.Pop();
            slot = slots[index];
        }
        else
        {
            index = slots.Count;
            if ((uint)index > IndexMask)
            {
                throw new InvalidOperationException("Too many host resources in one store.");
            }

            slot = new Slot { Generation = 1 };
            slots.Add(slot);
        }

        slot.Value = value;
        slot.Type = type;
        slot.State = state;
        slot.Lends = state == SlotState.Lent ? 1 : 0;
        slots[index] = slot;
        live++;
        return (uint)index | ((uint)slot.Generation << GenerationShift);
    }

    private object RemoveOwned(int index)
    {
        var value = slots[index].Value!;
        owned.Remove(value);
        guestOwned--;
        ForgetOwner(value);
        Release(index);
        return value;
    }

    private void Release(int index)
    {
        var slot = slots[index];
        slot.Value = null;
        slot.Type = null;
        slot.State = SlotState.Free;
        slot.Lends = 0;
        slot.Generation = (byte)(slot.Generation == byte.MaxValue ? 1 : slot.Generation + 1);
        slots[index] = slot;
        free.Push(index);
        live--;
    }

    private static void EndProcessLend(object value)
    {
        if (owners.TryGetValue(value, out var box))
        {
            lock (box)
            {
                box.Lends--;
            }
        }
    }

    private void ForgetOwner(object value)
    {
        if (owners.TryGetValue(value, out var box))
        {
            lock (box)
            {
                if (box.Owner is { } reference && reference.TryGetTarget(out var owner) && ReferenceEquals(owner, this))
                {
                    box.Owner = null;
                }
            }
        }
    }

    private bool TryFind(uint rep, out int index)
    {
        index = (int)(rep & IndexMask);
        var generation = (byte)(rep >> GenerationShift);
        return generation != 0 && index < slots.Count && slots[index].State != SlotState.Free &&
            slots[index].Generation == generation;
    }

    private int Find(ComponentHostResourceType type, uint rep)
    {
        ThrowIfDead();
        if (!TryFind(rep, out var index))
        {
            throw new InvalidOperationException(
                $"Host resource handle {rep} of type {type.DisplayName} is unknown to this store.");
        }

        if (!ReferenceEquals(slots[index].Type, type))
        {
            throw new InvalidOperationException(
                $"Host resource handle {rep} has type {slots[index].Type!.DisplayName}, not {type.DisplayName}.");
        }

        return index;
    }
}
