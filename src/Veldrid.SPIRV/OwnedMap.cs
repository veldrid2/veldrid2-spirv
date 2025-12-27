using System;
using System.Collections.Generic;

namespace Veldrid.SPIRV;

internal struct OwnedMap<K, V> : IDisposable
    where K : notnull
{
    public Dictionary<K, V> Map;

    public OwnedMap(Dictionary<K, V> map)
    {
        Map = map ?? throw new ArgumentNullException(nameof(map));
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
}
