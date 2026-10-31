using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FASTER.Services.A3S.Serialization
{
    /// <summary>
    /// Minimal Java <c>ObjectInputStream</c> reader for classic Arma3Sync GZIP ObjectStream graphs.
    /// Handle assignment follows OpenJDK order so <c>TC_REFERENCE</c> back-links resolve correctly.
    /// </summary>
    internal sealed class JavaObjectInputStream : IDisposable
    {
        private const ushort StreamMagic = 0xACED;
        private const ushort StreamVersion = 5;

        private const byte TcNull = 0x70;
        private const byte TcReference = 0x71;
        private const byte TcClassDesc = 0x72;
        private const byte TcObject = 0x73;
        private const byte TcString = 0x74;
        private const byte TcArray = 0x75;
        private const byte TcClass = 0x76;
        private const byte TcBlockData = 0x77;
        private const byte TcEndBlockData = 0x78;
        private const byte TcReset = 0x79;
        private const byte TcBlockDataLong = 0x7A;
        private const byte TcException = 0x7B;
        private const byte TcLongString = 0x7C;
        private const byte TcProxyClassDesc = 0x7D;
        private const byte TcEnum = 0x7E;

        private const byte ScWriteMethod = 0x01;
        private const byte ScSerializable = 0x02;
        private const byte ScExternalizable = 0x04;
        private const byte ScEnum = 0x10;

        private readonly BinaryReader _reader;
        private readonly Dictionary<int, object?> _handles = new();
        private int _nextHandle = 0x7E0000;

        public JavaObjectInputStream(Stream stream)
        {
            // Caller owns GZIP unwrap; we need big-endian primitives.
            _reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var magic = ReadUInt16();
            var version = ReadUInt16();
            if (magic != StreamMagic || version != StreamVersion)
            {
                throw new InvalidDataException(
                    $"Not a Java ObjectStream (magic=0x{magic:X4}, version={version}).");
            }
        }

        public object? ReadObject() => ReadContent();

        public void Dispose() => _reader.Dispose();

        private object? ReadContent()
        {
            var tc = ReadByte();
            return tc switch
            {
                TcNull => null,
                TcReference => ResolveHandle(ReadInt32()),
                TcClassDesc => ReadClassDesc(alreadyReadTc: true),
                TcObject => ReadNewObject(),
                TcString => ReadNewString(longString: false),
                TcArray => ReadNewArray(),
                TcClass => ReadClassDesc(alreadyReadTc: false),
                TcBlockData => ReadRawBlock(ReadByte()),
                TcBlockDataLong => ReadRawBlock(ReadInt32()),
                TcEndBlockData => JavaEndBlockData.Instance,
                TcReset => throw new NotSupportedException("TC_RESET is not used by classic .a3s metadata."),
                TcException => throw new NotSupportedException("TC_EXCEPTION in ObjectStream."),
                TcLongString => ReadNewString(longString: true),
                TcProxyClassDesc => throw new NotSupportedException("TC_PROXYCLASSDESC is not used by classic .a3s metadata."),
                TcEnum => ReadNewEnum(),
                _ => throw new InvalidDataException($"Unknown ObjectStream type code 0x{tc:X2}.")
            };
        }

        private JavaClassDesc? ReadClassDesc(bool alreadyReadTc)
        {
            byte tc;
            if (alreadyReadTc)
                tc = TcClassDesc;
            else
                tc = ReadByte();

            if (tc == TcNull)
                return null;
            if (tc == TcReference)
                return (JavaClassDesc)ResolveHandle(ReadInt32())!;
            if (tc == TcProxyClassDesc)
                throw new NotSupportedException("Proxy class descriptors are not supported.");
            if (tc != TcClassDesc)
                throw new InvalidDataException($"Expected TC_CLASSDESC, got 0x{tc:X2}.");

            var name = ReadUtf();
            var suid = ReadInt64();
            var flags = ReadByte();
            var desc = new JavaClassDesc(name, suid, flags);
            // OpenJDK assigns the handle after flags, before fields.
            AssignHandle(desc);

            var fieldCount = ReadUInt16();
            var fields = new JavaFieldDesc[fieldCount];
            for (var i = 0; i < fieldCount; i++)
            {
                var typeCode = (char)ReadByte();
                var fieldName = ReadUtf();
                object? typeString = null;
                if (typeCode is 'L' or '[')
                    typeString = ReadContent();
                fields[i] = new JavaFieldDesc(fieldName, typeCode, typeString as string);
            }

            desc.Fields = fields;

            // classAnnotation … TC_ENDBLOCKDATA
            while (true)
            {
                var ann = ReadContent();
                if (ReferenceEquals(ann, JavaEndBlockData.Instance))
                    break;
            }

            desc.SuperClass = ReadClassDesc(alreadyReadTc: false);
            return desc;
        }

        private object ReadNewObject()
        {
            var classDesc = ReadClassDesc(alreadyReadTc: false)
                            ?? throw new InvalidDataException("TC_OBJECT without class descriptor.");
            var obj = new JavaObject(classDesc.Name);
            AssignHandle(obj);

            foreach (var cls in classDesc.Hierarchy())
                ReadClassData(cls, obj);

            return NormalizeJdkObject(obj);
        }

        private void ReadClassData(JavaClassDesc cls, JavaObject obj)
        {
            if ((cls.Flags & ScExternalizable) != 0)
            {
                while (!ReferenceEquals(ReadContent(), JavaEndBlockData.Instance))
                {
                    // discard externalizable block contents
                }

                return;
            }

            if ((cls.Flags & ScSerializable) == 0 && (cls.Flags & ScEnum) == 0)
                return;

            foreach (var field in cls.Fields)
            {
                object? value = field.TypeCode switch
                {
                    'Z' => ReadByte() != 0,
                    'B' => (sbyte)ReadByte(),
                    'C' => (char)ReadUInt16(),
                    'S' => ReadInt16(),
                    'I' => ReadInt32(),
                    'J' => ReadInt64(),
                    'F' => ReadSingle(),
                    'D' => ReadDouble(),
                    'L' or '[' => ReadContent(),
                    _ => throw new InvalidDataException($"Unsupported field type '{field.TypeCode}' on {cls.Name}.{field.Name}.")
                };
                obj.Fields[field.Name] = value;
            }

            if ((cls.Flags & ScWriteMethod) != 0)
            {
                var custom = new List<object?>();
                while (true)
                {
                    var item = ReadContent();
                    if (ReferenceEquals(item, JavaEndBlockData.Instance))
                        break;
                    custom.Add(item);
                }

                obj.CustomData = custom;
            }
        }

        private object NormalizeJdkObject(JavaObject obj)
        {
            switch (obj.ClassName)
            {
                case "java.util.Date":
                {
                    var millis = TryReadBlockInt64(obj.CustomData);
                    if (millis == null)
                        return DateTime.UnixEpoch;
                    return DateTimeOffset.FromUnixTimeMilliseconds(millis.Value).UtcDateTime;
                }
                case "java.util.ArrayList":
                {
                    var list = new List<object?>();
                    if (obj.CustomData != null)
                    {
                        foreach (var item in obj.CustomData)
                        {
                            if (item is byte[])
                                continue; // capacity / size block
                            list.Add(item);
                        }
                    }

                    return list;
                }
                case "java.util.HashSet":
                case "java.util.LinkedHashSet":
                {
                    var set = new List<object?>();
                    if (obj.CustomData != null)
                    {
                        foreach (var item in obj.CustomData)
                        {
                            if (item is byte[])
                                continue; // capacity / loadFactor / size
                            set.Add(item);
                        }
                    }

                    return set;
                }
                case "java.util.HashMap":
                case "java.util.LinkedHashMap":
                {
                    var map = new Dictionary<object, object?>();
                    if (obj.CustomData == null || obj.CustomData.Count == 0)
                        return map;

                    var i = 0;
                    if (obj.CustomData[0] is byte[])
                        i = 1;
                    for (; i + 1 < obj.CustomData.Count; i += 2)
                    {
                        var key = obj.CustomData[i];
                        if (key == null)
                            continue;
                        map[key] = obj.CustomData[i + 1];
                    }

                    return map;
                }
                case "java.lang.Boolean":
                    return obj.Fields.TryGetValue("value", out var bv) && bv is bool b && b;
                case "java.lang.Integer":
                    return obj.Fields.TryGetValue("value", out var iv) && iv is int ii ? ii : 0;
                case "java.lang.Long":
                    return obj.Fields.TryGetValue("value", out var lv) && lv is long ll ? ll : 0L;
                case "java.lang.String":
                    // Should appear as TC_STRING, not as object; keep defensive.
                    return obj.Fields.TryGetValue("value", out var sv) ? sv ?? string.Empty : string.Empty;
                default:
                    return obj;
            }
        }

        private static long? TryReadBlockInt64(List<object?>? custom)
        {
            if (custom == null)
                return null;
            foreach (var item in custom)
            {
                if (item is byte[] { Length: >= 8 } bytes)
                {
                    // big-endian
                    long v = 0;
                    for (var i = 0; i < 8; i++)
                        v = (v << 8) | bytes[i];
                    return v;
                }
            }

            return null;
        }

        private object ReadNewArray()
        {
            var classDesc = ReadClassDesc(alreadyReadTc: false)
                            ?? throw new InvalidDataException("TC_ARRAY without class descriptor.");
            var size = ReadInt32();
            if (size < 0)
                throw new InvalidDataException("Negative array length.");

            var typeName = classDesc.Name;
            if (typeName.Length == 2 && typeName[0] == '[')
            {
                var code = typeName[1];
                object arr = code switch
                {
                    'Z' => ReadPrimitiveBoolArray(size),
                    'B' => _reader.ReadBytes(size),
                    'C' => ReadPrimitiveCharArray(size),
                    'S' => ReadPrimitiveShortArray(size),
                    'I' => ReadPrimitiveIntArray(size),
                    'J' => ReadPrimitiveLongArray(size),
                    'F' => ReadPrimitiveFloatArray(size),
                    'D' => ReadPrimitiveDoubleArray(size),
                    _ => throw new InvalidDataException($"Unsupported primitive array '{typeName}'.")
                };
                AssignHandle(arr);
                return arr;
            }

            var list = new List<object?>(size);
            AssignHandle(list);
            for (var i = 0; i < size; i++)
                list.Add(ReadContent());
            return list;
        }

        private bool[] ReadPrimitiveBoolArray(int size)
        {
            var a = new bool[size];
            for (var i = 0; i < size; i++)
                a[i] = ReadByte() != 0;
            return a;
        }

        private char[] ReadPrimitiveCharArray(int size)
        {
            var a = new char[size];
            for (var i = 0; i < size; i++)
                a[i] = (char)ReadUInt16();
            return a;
        }

        private short[] ReadPrimitiveShortArray(int size)
        {
            var a = new short[size];
            for (var i = 0; i < size; i++)
                a[i] = ReadInt16();
            return a;
        }

        private int[] ReadPrimitiveIntArray(int size)
        {
            var a = new int[size];
            for (var i = 0; i < size; i++)
                a[i] = ReadInt32();
            return a;
        }

        private long[] ReadPrimitiveLongArray(int size)
        {
            var a = new long[size];
            for (var i = 0; i < size; i++)
                a[i] = ReadInt64();
            return a;
        }

        private float[] ReadPrimitiveFloatArray(int size)
        {
            var a = new float[size];
            for (var i = 0; i < size; i++)
                a[i] = ReadSingle();
            return a;
        }

        private double[] ReadPrimitiveDoubleArray(int size)
        {
            var a = new double[size];
            for (var i = 0; i < size; i++)
                a[i] = ReadDouble();
            return a;
        }

        private object ReadNewEnum()
        {
            var classDesc = ReadClassDesc(alreadyReadTc: false)
                            ?? throw new InvalidDataException("TC_ENUM without class descriptor.");
            var enm = new JavaEnum(classDesc.Name, string.Empty);
            AssignHandle(enm);
            var nameObj = ReadContent();
            enm.Name = nameObj as string
                       ?? throw new InvalidDataException("Enum constant name was not a string.");
            return enm;
        }

        private string ReadNewString(bool longString)
        {
            string s;
            if (longString)
            {
                var len = ReadInt64();
                if (len < 0 || len > int.MaxValue)
                    throw new InvalidDataException("Invalid long UTF length.");
                s = Encoding.UTF8.GetString(_reader.ReadBytes((int)len));
            }
            else
            {
                s = ReadUtf();
            }

            AssignHandle(s);
            return s;
        }

        private byte[] ReadRawBlock(int length)
        {
            if (length < 0)
                throw new InvalidDataException("Negative block data length.");
            return _reader.ReadBytes(length);
        }

        private int AssignHandle(object? obj)
        {
            var h = _nextHandle++;
            _handles[h] = obj;
            return h;
        }

        private object? ResolveHandle(int handle)
        {
            if (!_handles.TryGetValue(handle, out var obj))
                throw new InvalidDataException($"Invalid ObjectStream handle 0x{handle:X}.");
            return obj;
        }

        private byte ReadByte() => _reader.ReadByte();

        private ushort ReadUInt16()
        {
            var b0 = _reader.ReadByte();
            var b1 = _reader.ReadByte();
            return (ushort)((b0 << 8) | b1);
        }

        private short ReadInt16() => (short)ReadUInt16();

        private int ReadInt32()
        {
            var b0 = _reader.ReadByte();
            var b1 = _reader.ReadByte();
            var b2 = _reader.ReadByte();
            var b3 = _reader.ReadByte();
            return (b0 << 24) | (b1 << 16) | (b2 << 8) | b3;
        }

        private long ReadInt64()
        {
            long v = 0;
            for (var i = 0; i < 8; i++)
                v = (v << 8) | _reader.ReadByte();
            return v;
        }

        private float ReadSingle()
        {
            var bytes = _reader.ReadBytes(4);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToSingle(bytes, 0);
        }

        private double ReadDouble()
        {
            var bytes = _reader.ReadBytes(8);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return BitConverter.ToDouble(bytes, 0);
        }

        private string ReadUtf()
        {
            var len = ReadUInt16();
            var bytes = _reader.ReadBytes(len);
            return Encoding.UTF8.GetString(bytes);
        }
    }

    internal sealed class JavaEndBlockData
    {
        public static readonly JavaEndBlockData Instance = new();
        private JavaEndBlockData() { }
    }

    internal sealed class JavaClassDesc
    {
        public JavaClassDesc(string name, long serialVersionUid, byte flags)
        {
            Name = name;
            SerialVersionUid = serialVersionUid;
            Flags = flags;
            Fields = Array.Empty<JavaFieldDesc>();
        }

        public string Name { get; }
        public long SerialVersionUid { get; }
        public byte Flags { get; }
        public JavaFieldDesc[] Fields { get; set; }
        public JavaClassDesc? SuperClass { get; set; }

        public IEnumerable<JavaClassDesc> Hierarchy()
        {
            var stack = new Stack<JavaClassDesc>();
            for (var c = this; c != null; c = c.SuperClass)
                stack.Push(c);
            while (stack.Count > 0)
                yield return stack.Pop();
        }
    }

    internal readonly struct JavaFieldDesc
    {
        public JavaFieldDesc(string name, char typeCode, string? typeString)
        {
            Name = name;
            TypeCode = typeCode;
            TypeString = typeString;
        }

        public string Name { get; }
        public char TypeCode { get; }
        public string? TypeString { get; }
    }

    internal sealed class JavaObject
    {
        public JavaObject(string className) => ClassName = className;
        public string ClassName { get; }
        public Dictionary<string, object?> Fields { get; } = new(StringComparer.Ordinal);
        public List<object?>? CustomData { get; set; }

        public T? Get<T>(string name)
        {
            if (!Fields.TryGetValue(name, out var value) || value is null)
                return default;
            if (value is T t)
                return t;
            return default;
        }
    }

    internal sealed class JavaEnum
    {
        public JavaEnum(string className, string name)
        {
            ClassName = className;
            Name = name;
        }

        public string ClassName { get; }
        public string Name { get; set; }
    }
}
