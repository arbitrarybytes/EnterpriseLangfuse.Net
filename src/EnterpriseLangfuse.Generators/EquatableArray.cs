using System;
using System.Collections;
using System.Collections.Generic;

namespace EnterpriseLangfuse.Generators;

/// <summary>
/// An immutable array with structural equality.
/// </summary>
/// <remarks>
/// Roslyn caches every step of an incremental pipeline by comparing the previous and current models
/// with <see cref="object.Equals(object)"/>. A plain array or list compares by reference, so a model
/// holding one would report "changed" on every keystroke and defeat the cache entirely. This type is
/// the standard workaround: it gives a record's compiler-generated equality something value-based to
/// call.
/// </remarks>
/// <typeparam name="T">The element type, itself compared structurally.</typeparam>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
    where T : IEquatable<T>
{
    private readonly T[]? _items;

    public EquatableArray(T[] items) => _items = items;

    public int Count => _items?.Length ?? 0;

    public T this[int index] => (_items ?? throw new IndexOutOfRangeException())[index];

    public bool Equals(EquatableArray<T> other)
    {
        var left = _items ?? Array.Empty<T>();
        var right = other._items ?? Array.Empty<T>();

        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 17;
        foreach (var item in _items ?? Array.Empty<T>())
        {
            hash = (hash * 31) + (item?.GetHashCode() ?? 0);
        }

        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? Array.Empty<T>())).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
