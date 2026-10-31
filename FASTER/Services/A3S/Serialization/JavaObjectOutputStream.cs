using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FASTER.Services.A3S.Serialization
{
    /// <summary>
    /// Minimal Java <c>ObjectOutputStream</c> writer matching OpenJDK handle assignment
    /// for classic Arma3Sync domain graphs.
    /// </summary>
    internal sealed class JavaObjectOutputStream : IDisposable
    {
        private const ushort StreamMagic = 0xACED;
        private const ushort StreamVersion = 5;

        private const byte TcNull = 0x70;
        private const byte TcReference = 0x71;
        private const byte TcClassDesc = 0x72;
        private const byte TcObject = 0x73;
        private const byte TcString = 0x74;
        private const byte TcBlockData = 0x77;
        private const byte TcEndBlockData = 0x78;
        private const byte TcBlockDataLong = 0x7A;
        private const byte TcEnum = 0x7E;

        private const byte ScWriteMethod = 0x01;
        private const byte ScSerializable = 0x02;
        private const byte ScEnum = 0x10;

        private readonly Stream _stream;
        private readonly Dictionary<object, int> _handles = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, int> _classDescHandles = new(StringComparer.Ordinal);
        private int _nextHandle = 0x7E0000;

        public JavaObjectOutputStream(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            WriteUInt16(StreamMagic);
            WriteUInt16(StreamVersion);
        }

        public void WriteObject(object? value) => WriteContent(value);

        public void Dispose()
        {
        }

        private void WriteContent(object? value)
        {
            if (value is null)
            {
                WriteByte(TcNull);
                return;
            }

            if (_handles.TryGetValue(value, out var existing))
            {
                WriteByte(TcReference);
                WriteInt32(existing);
                return;
            }

            switch (value)
            {
                case string s:
                    WriteNewString(s);
                    return;
                case JavaEnum enm:
                    WriteNewEnum(enm);
                    return;
                case JavaObject obj:
                    WriteNewObject(obj);
                    return;
                default:
                    throw new NotSupportedException($"Cannot serialize type {value.GetType().FullName}.");
            }
        }

        private void WriteNewString(string s)
        {
            WriteByte(TcString);
            AssignHandle(s);
            WriteUtf(s);
        }

        private void WriteNewEnum(JavaEnum enm)
        {
            var classDesc = enm.ClassDesc
                            ?? throw new InvalidOperationException($"Enum {enm.ClassName} missing ClassDesc.");
            WriteByte(TcEnum);
            WriteClassDesc(classDesc);
            AssignHandle(enm);
            WriteContent(enm.Name);
        }

        private void WriteNewObject(JavaObject obj)
        {
            var classDesc = obj.ClassDesc
                            ?? throw new InvalidOperationException($"Object {obj.ClassName} missing ClassDesc.");
            WriteByte(TcObject);
            WriteClassDesc(classDesc);
            AssignHandle(obj);

            foreach (var cls in classDesc.Hierarchy())
                WriteClassData(cls, obj);
        }

        private void WriteClassData(JavaClassDesc cls, JavaObject obj)
        {
            if ((cls.Flags & ScSerializable) == 0 && (cls.Flags & ScEnum) == 0)
                return;

            foreach (var field in cls.Fields)
            {
                obj.Fields.TryGetValue(field.Name, out var value);
                switch (field.TypeCode)
                {
                    case 'Z':
                        WriteByte(value is true ? (byte)1 : (byte)0);
                        break;
                    case 'B':
                        WriteByte(Convert.ToByte(value ?? 0));
                        break;
                    case 'C':
                        WriteUInt16(Convert.ToUInt16(value ?? 0));
                        break;
                    case 'S':
                        WriteInt16(Convert.ToInt16(value ?? 0));
                        break;
                    case 'I':
                        WriteInt32(Convert.ToInt32(value ?? 0));
                        break;
                    case 'J':
                        WriteInt64(Convert.ToInt64(value ?? 0L));
                        break;
                    case 'F':
                        WriteSingle(Convert.ToSingle(value ?? 0f));
                        break;
                    case 'D':
                        WriteDouble(Convert.ToDouble(value ?? 0d));
                        break;
                    case 'L':
                    case '[':
                        WriteContent(value);
                        break;
                    default:
                        throw new InvalidDataException($"Unsupported field type '{field.TypeCode}'.");
                }
            }

            if ((cls.Flags & ScWriteMethod) != 0)
            {
                // Only the declaring class's writeObject runs — invoke when this is the object's own class.
                if (ReferenceEquals(cls, obj.ClassDesc))
                    obj.WriteCustom?.Invoke(this);
                WriteByte(TcEndBlockData);
            }
        }

        private void WriteClassDesc(JavaClassDesc? desc)
        {
            if (desc is null)
            {
                WriteByte(TcNull);
                return;
            }

            if (_classDescHandles.TryGetValue(desc.Name, out var existing))
            {
                WriteByte(TcReference);
                WriteInt32(existing);
                return;
            }

            WriteByte(TcClassDesc);
            WriteUtf(desc.Name);
            WriteInt64(desc.SerialVersionUid);
            WriteByte(desc.Flags);
            AssignHandle(desc);
            _classDescHandles[desc.Name] = _nextHandle - 1;

            WriteUInt16((ushort)desc.Fields.Length);
            foreach (var field in desc.Fields)
            {
                WriteByte((byte)field.TypeCode);
                WriteUtf(field.Name);
                if (field.TypeCode is 'L' or '[')
                {
                    if (string.IsNullOrEmpty(field.TypeString))
                        throw new InvalidDataException($"Object field {desc.Name}.{field.Name} requires a type string.");
                    // Type strings are ordinary String objects (may be referenced later).
                    WriteContent(field.TypeString);
                }
            }

            WriteByte(TcEndBlockData);
            WriteClassDesc(desc.SuperClass);
        }

        public void WriteBlockData(byte[] data)
        {
            if (data.Length <= 255)
            {
                WriteByte(TcBlockData);
                WriteByte((byte)data.Length);
            }
            else
            {
                WriteByte(TcBlockDataLong);
                WriteInt32(data.Length);
            }

            _stream.Write(data, 0, data.Length);
        }

        public void WriteBlockInt32(int value)
        {
            WriteBlockData(new[]
            {
                (byte)(value >> 24),
                (byte)(value >> 16),
                (byte)(value >> 8),
                (byte)value
            });
        }

        public void WriteBlockInt64(long value)
        {
            var buf = new byte[8];
            for (var i = 7; i >= 0; i--)
            {
                buf[i] = (byte)(value & 0xFF);
                value >>= 8;
            }

            WriteBlockData(buf);
        }

        public void WriteBlockHashSetHeader(int capacity, float loadFactor, int size)
        {
            var buf = new byte[12];
            buf[0] = (byte)(capacity >> 24);
            buf[1] = (byte)(capacity >> 16);
            buf[2] = (byte)(capacity >> 8);
            buf[3] = (byte)capacity;
            var fbits = BitConverter.GetBytes(loadFactor);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(fbits);
            Buffer.BlockCopy(fbits, 0, buf, 4, 4);
            buf[8] = (byte)(size >> 24);
            buf[9] = (byte)(size >> 16);
            buf[10] = (byte)(size >> 8);
            buf[11] = (byte)size;
            WriteBlockData(buf);
        }

        public void WriteBlockHashMapHeader(int buckets, int size)
        {
            WriteBlockData(new[]
            {
                (byte)(buckets >> 24),
                (byte)(buckets >> 16),
                (byte)(buckets >> 8),
                (byte)buckets,
                (byte)(size >> 24),
                (byte)(size >> 16),
                (byte)(size >> 8),
                (byte)size
            });
        }

        private int AssignHandle(object obj)
        {
            var h = _nextHandle++;
            _handles[obj] = h;
            return h;
        }

        private void WriteByte(byte b) => _stream.WriteByte(b);

        private void WriteUInt16(ushort v)
        {
            _stream.WriteByte((byte)(v >> 8));
            _stream.WriteByte((byte)v);
        }

        private void WriteInt16(short v) => WriteUInt16(unchecked((ushort)v));

        private void WriteInt32(int v)
        {
            _stream.WriteByte((byte)(v >> 24));
            _stream.WriteByte((byte)(v >> 16));
            _stream.WriteByte((byte)(v >> 8));
            _stream.WriteByte((byte)v);
        }

        private void WriteInt64(long v)
        {
            for (var i = 7; i >= 0; i--)
                _stream.WriteByte((byte)((v >> (8 * i)) & 0xFF));
        }

        private void WriteSingle(float v)
        {
            var bytes = BitConverter.GetBytes(v);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            _stream.Write(bytes, 0, 4);
        }

        private void WriteDouble(double v)
        {
            var bytes = BitConverter.GetBytes(v);
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            _stream.Write(bytes, 0, 8);
        }

        private void WriteUtf(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            if (bytes.Length > ushort.MaxValue)
                throw new InvalidDataException("UTF string too long for modified UTF-8 short form.");
            WriteUInt16((ushort)bytes.Length);
            _stream.Write(bytes, 0, bytes.Length);
        }
    }

    /// <summary>Reference equality for handle tables (Java identity semantics).</summary>
    internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
