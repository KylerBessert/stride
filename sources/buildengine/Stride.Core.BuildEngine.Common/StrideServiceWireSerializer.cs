// Copyright (c) .NET Foundation and Contributors (https://dotnetfoundation.org/ & https://stride3d.net) and Silicon Studio Corp. (https://www.siliconstudio.co.jp)
// Distributed under the MIT license. See the LICENSE.md file in the project root for more information.

using System.Text;
using System.Text.Json;
using ServiceWire;
using Stride.Core.Serialization;
using Stride.Core.Serialization.Contents;

namespace Stride.Core.BuildEngine;

public class StrideServiceWireSerializer : ISerializer
{
    public T? Deserialize<T>(byte[] bytes)
    {
        if (null == bytes || bytes.Length == 0) return default;
        var reader = new BinarySerializationReader(new MemoryStream(bytes));
        reader.Context.SerializerSelector = SerializerSelector.AssetWithReuse;
        reader.Context.Set(ContentSerializerContext.SerializeAttachedReferenceProperty, ContentSerializerContext.AttachedReferenceSerialization.AsSerializableVersion);
        T? command = default;
        reader.SerializeExtended(ref command, ArchiveMode.Deserialize, null);
        return command;
    }

    public object? Deserialize(byte[] bytes, string typeConfigName)
    {
        ArgumentNullException.ThrowIfNull(typeConfigName);
        var type = typeConfigName.ToType();
        if (null == typeConfigName || null == bytes || bytes.Length == 0) return type.GetDefault();
        var reader = new BinarySerializationReader(new MemoryStream(bytes));
        reader.Context.SerializerSelector = SerializerSelector.AssetWithReuse;
        reader.Context.Set(ContentSerializerContext.SerializeAttachedReferenceProperty, ContentSerializerContext.AttachedReferenceSerialization.AsSerializableVersion);
        object? command = null;
        reader.SerializeExtended(ref command, ArchiveMode.Deserialize, null);
        return command;
    }

    public byte[]? Serialize<T>(T obj)
    {
        if (null == obj) return null;
        var memoryStream = new MemoryStream();
        var writer = new BinarySerializationWriter(memoryStream);
        writer.Context.SerializerSelector = SerializerSelector.AssetWithReuse;
        writer.Context.Set(ContentSerializerContext.SerializeAttachedReferenceProperty, ContentSerializerContext.AttachedReferenceSerialization.AsSerializableVersion);
        writer.SerializeExtended(ref obj, ArchiveMode.Serialize);

        return memoryStream.ToArray();
    }

    public byte[]? Serialize(object obj, string typeConfigName)
    {
        if (null == obj) return null;
        var type = typeConfigName.ToType();
        var memoryStream = new MemoryStream();
        var writer = new BinarySerializationWriter(memoryStream);
        writer.Context.SerializerSelector = SerializerSelector.AssetWithReuse;
        writer.Context.Set(ContentSerializerContext.SerializeAttachedReferenceProperty, ContentSerializerContext.AttachedReferenceSerialization.AsSerializableVersion);
        writer.SerializeExtended(ref obj, ArchiveMode.Serialize);

        return memoryStream.ToArray();
    }
}

public class NewtonsoftSerializer : ISerializer
{
    //private JsonSerializerSettings settings = new JsonSerializerSettings
    //{
    //    ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore
    //};
    private readonly JsonSerializerOptions settings = new()
    {
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles
    };

    public T? Deserialize<T>(byte[] bytes)
    {
        if (null == bytes || bytes.Length == 0) return default;
        var json = Encoding.UTF8.GetString(bytes);

        return JsonSerializer.Deserialize<T>(json, settings); //return JsonConvert.DeserializeObject<T>(json, settings);
    }

    public object? Deserialize(byte[] bytes, string typeConfigName)
    {
        ArgumentNullException.ThrowIfNull(typeConfigName);
        var type = typeConfigName.ToType();
        if (null == typeConfigName || null == bytes || bytes.Length == 0) return type.GetDefault();
        var json = Encoding.UTF8.GetString(bytes);
        return JsonSerializer.Deserialize(json,type, settings); //return JsonConvert.DeserializeObject(json, type, settings);
    }

    public byte[]? Serialize<T>(T obj)
    {
        if (null == obj) return null;
        var json = JsonSerializer.Serialize(obj, settings); //JsonConvert.SerializeObject(obj, settings);
        return Encoding.UTF8.GetBytes(json);
    }

    public byte[]? Serialize(object obj, string typeConfigName)
    {
        if (null == obj) return null;
        var type = typeConfigName.ToType();
        var json = JsonSerializer.Serialize(obj, type, settings); //JsonConvert.SerializeObject(obj, type, settings);
        return Encoding.UTF8.GetBytes(json);
    }
}
