using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace CpuEmulator.Generators;

/// <summary>
/// Value-equatable wrapper over <see cref="ImmutableArray{T}"/> for incremental pipeline
/// state. <see cref="ImmutableArray{T}.Equals(ImmutableArray{T})"/> is reference equality
/// of the backing array, so record-synthesized equality over raw ImmutableArray fields
/// never compares equal across reparses — defeating incremental caching. This wrapper
/// compares element-wise.
/// </summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _values;

    public EquatableArray(ImmutableArray<T> values) => _values = values;

    /// <summary>Backing array, normalizing default (uninitialized) to empty.</summary>
    public ImmutableArray<T> Values => _values.IsDefault ? ImmutableArray<T>.Empty : _values;

    public int Length => _values.IsDefault ? 0 : _values.Length;

    public T this[int index] => Values[index];

    public static implicit operator EquatableArray<T>(ImmutableArray<T> values) => new(values);

    public bool Equals(EquatableArray<T> other)
    {
        var self = Values;
        var theirs = other.Values;
        if (self.Length != theirs.Length)
            return false;
        for (int i = 0; i < self.Length; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(self[i], theirs[i]))
                return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            foreach (var item in Values)
                hash = hash * 31 + (item?.GetHashCode() ?? 0);
            return hash;
        }
    }

    /// <summary>Struct enumerator for allocation-free foreach.</summary>
    public ImmutableArray<T>.Enumerator GetEnumerator() => Values.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)Values).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<T>)Values).GetEnumerator();
}
