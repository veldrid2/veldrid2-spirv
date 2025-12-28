using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Veldrid.SPIRV;

internal struct OwnedMap<K, V> : IDisposable
    where K : notnull
{
    public Dictionary<K, V> Map;

    public OwnedMap(Dictionary<K, V> map)
    {
        Map = map ?? throw new ArgumentNullException(nameof(map));
    }

    public readonly OrderedEnumerator GetEnumerator()
    {
        return new OrderedEnumerator(Map);
    }

    public void Dispose()
    {
        if (Map == null)
        {
            return;
        }

        foreach (KeyValuePair<K, V> pair in Map)
        {
            if (pair.Key is IDisposable)
            {
                ((IDisposable)pair.Key).Dispose();
            }
            if (pair.Value is IDisposable)
            {
                ((IDisposable)pair.Value).Dispose();
            }
        }

        Map = null!;
    }

    public struct OrderedEnumerator : IDisposable
    {
        private readonly Dictionary<K, V> _map;
        private readonly K[] _sortedKeys;
        private int _index;

        public OrderedEnumerator(Dictionary<K, V> map)
        {
            _map = map;
            _sortedKeys = map.Keys.ToArray();
            _sortedKeys.AsSpan().Sort();
            _index = 0;
        }

        public readonly Pair Current
        {
            get
            {
                ref K key = ref _sortedKeys[_index - 1];
                return new Pair(in key, ref CollectionsMarshal.GetValueRefOrNullRef(_map, key));
            }
        }

        public bool MoveNext()
        {
            if ((uint)_index < (uint)_sortedKeys.Length)
            {
                _index++;
                return true;
            }
            return false;
        }

        public void Dispose()
        {
        }
    }

    public readonly ref struct Pair
    {
        public readonly ref readonly K Key;
        public readonly ref V Value;

        public Pair(ref readonly K key, ref V value)
        {
            Key = ref key;
            Value = ref value;
        }
    }
}
