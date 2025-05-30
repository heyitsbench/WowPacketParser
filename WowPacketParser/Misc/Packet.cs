using Google.Protobuf.WellKnownTypes;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using WowPacketParser.Enums;
using WowPacketParser.Enums.Version;
using WowPacketParser.Parsing.Parsers;
using WowPacketParser.Proto;
using WowPacketParser.Store;
using WowPacketParser.Store.Objects;

namespace WowPacketParser.Misc
{
    public sealed partial class Packet : BinaryReader
    {
        private static readonly bool SniffData = Settings.SQLOutputFlag.HasAnyFlagBit(SQLOutput.SniffData) || Settings.DumpFormat == DumpFormatType.SniffDataOnly;
        private static readonly bool SniffDataOpcodes = Settings.SQLOutputFlag.HasAnyFlagBit(SQLOutput.SniffDataOpcodes) || Settings.DumpFormat == DumpFormatType.SniffDataOnly;

        private static DateTime _firstPacketTime;

        [SuppressMessage("Microsoft.Reliability", "CA2000", Justification = "MemoryStream is disposed in ClosePacket().")]
        public Packet(byte[] input, int opcode, DateTime time, Direction direction, int number, StringBuilder writer, string fileName)
            : base(new MemoryStream(input, 0, input.Length), Encoding.UTF8)
        {
            Opcode = opcode;
            Time = time;
            Direction = direction;
            Number = number;
            Writer = writer;
            FileName = fileName;
            Status = ParsedStatus.None;
            WriteToFile = true;

            if (number == 0)
                _firstPacketTime = Time;

            TimeSpan = Time - _firstPacketTime;

            Holder = new PacketHolder()
            {
                BaseData = new PacketBase()
                {
                    Number = number,
                    Time = Timestamp.FromDateTime(DateTime.SpecifyKind(time, DateTimeKind.Utc)),
                    Opcode = Opcodes.GetOpcodeName(Opcode, Direction, false)
                }
            };
        }

        [SuppressMessage("Microsoft.Reliability", "CA2000", Justification = "MemoryStream is disposed in ClosePacket().")]
        public Packet(byte[] input, int opcode, DateTime time, Direction direction, int number, string fileName)
            : this(input, opcode, time, direction, number, null, fileName)
        {
        }

        public int Opcode { get; set; } // setter can't be private because it's used in multiple_packets
        public DateTime Time { get; }
        public TimeSpan TimeSpan { get; }
        public Direction Direction { get; }
        public int Number { get; }
        public StringBuilder Writer { get; private set; }
        public string FileName { get; }
        public ParsedStatus Status { get; set; }
        public bool WriteToFile { get; private set; }
        public int ConnectionIndex { get; set; }
        public IPEndPoint EndPoint { get; set; }

        public PacketHolder Holder { get; set; }

        public void AddSniffData(StoreNameType type, int id, string data)
        {
            if (type == StoreNameType.None)
                return;

            if (id == 0 && type != StoreNameType.Map)
                return; // Only maps can have id 0

            if (type == StoreNameType.Opcode && !SniffDataOpcodes)
                return; // Don't add opcodes if its config is not enabled

            if (type != StoreNameType.Opcode && !SniffData)
                return;

            SniffData item = new SniffData
            {
                SniffName = FileName,
                ObjectType = type,
                Id = id,
                Data = data
            };

            Storage.SniffData.Add(item, TimeSpan);
        }

        public void ResetInflateStream() => ZLibHelper.ResetInflateStream(ConnectionIndex);

        public Packet Inflate(int inflatedSize, bool keepStream = true)
        {
            var arr = ReadToEnd();
            var newarr = new byte[inflatedSize];

            if (ClientVersion.RemovedInVersion(ClientVersionBuild.V4_3_0_15005))
                keepStream = false;

            if (keepStream)
            {
                int idx = ConnectionIndex;
                while (!ZLibHelper.TryInflate(inflatedSize, idx, arr, ref newarr) && idx <= 4)
                    idx += 1;
            }
            else
            {
                ZLibHelper.Inflate(inflatedSize, arr, newarr);
            }

            // Cannot use "using" here
            var pkt = new Packet(newarr, Opcode, Time, Direction, Number, Writer, FileName)
            {
                ConnectionIndex = ConnectionIndex
            };
            return pkt;
        }

        public Packet Inflate(int arrSize, int inflatedSize, bool keepStream = true)
        {
            var arr = ReadBytes(arrSize);
            var newarr = new byte[inflatedSize];

            if (ClientVersion.RemovedInVersion(ClientVersionBuild.V4_3_0_15005))
                keepStream = false;

            if (keepStream)
            {
                int idx = ConnectionIndex;
                while (!ZLibHelper.TryInflate(inflatedSize, idx, arr, ref newarr) && idx <= 4)
                    idx += 1;
            }
            else
            {
                ZLibHelper.Inflate(inflatedSize, arr, newarr);
            }

            // Cannot use "using" here
            var pkt = new Packet(newarr, Opcode, Time, Direction, Number, Writer, FileName)
            {
                ConnectionIndex = ConnectionIndex
            };
            return pkt;
        }

        public byte[] GetStream(long offset)
        {
            var pos = Position;
            SetPosition(offset);
            var buffer = ReadToEnd();
            SetPosition(pos);
            return buffer;
        }

        public string GetHeader(bool isMultiple = false)
        {
            // ReSharper disable once UseStringInterpolation
            return string.Format("{0}: {1} (0x{2}) Length: {3} ConnIdx: {4}{5} Time: {6} Number: {7}{8}",
                Direction, Opcodes.GetOpcodeName(Opcode, Direction, false), Opcode.ToString("X4"),
                Length, ConnectionIndex, EndPoint != null ? " EP: " + EndPoint : "", Time.ToString("MM/dd/yyyy HH:mm:ss.fff"),
                Number, isMultiple ? " (part of another packet)" : "");
        }

        public long Position => BaseStream.Position;

        public void SetPosition(long val)
        {
            BaseStream.Position = val;
        }

        public long Length => BaseStream.Length;

        public bool CanRead()
        {
            return Position != Length;
        }

        public void Write(string value)
        {
            if (!Settings.DumpFormatWithText())
                return;

            Writer ??= new StringBuilder();
            Writer.Append(value);
        }

        public void Write(string format, params object[] args)
        {
            if (!Settings.DumpFormatWithText())
                return;

            Writer ??= new StringBuilder();
            Writer.AppendFormat(format, args);
        }

        public void WriteLine()
        {
            if (!Settings.DumpFormatWithText())
                return;

            Writer ??= new StringBuilder();
            Writer.AppendLine();
        }

        public void WriteLine(string value)
        {
            if (!Settings.DumpFormatWithText())
                return;

            Writer ??= new StringBuilder();
            Writer.AppendLine(value);
        }

        public void WriteLine(string format, params object[] args)
        {
            if (!Settings.DumpFormatWithText())
                return;

            Writer ??= new StringBuilder();
            Writer.AppendLine(string.Format(format, args));
        }

        public void ClosePacket(bool clearWriter = true)
        {
            if (clearWriter && Writer != null)
            {
                if (Settings.DumpFormatWithText())
                    Writer.Clear();
                Writer = null;
            }

            BaseStream.Close();

            Dispose(true);
        }

        public T AddValue<T>(string name, T obj, params object[] indexes)
        {
            if (!Settings.DumpFormatWithText())
                return obj;

            Writer ??= new StringBuilder();
            Writer.AppendLine($"{GetIndexString(indexes)}{name}: {obj}");

            return obj;
        }

        public void AddLine(string value, params object[] indexes)
        {
            if (!Settings.DumpFormatWithText())
                return;

            Writer ??= new StringBuilder();
            Writer.AppendLine($"{GetIndexString(indexes)}{value}");
        }
    }
}
