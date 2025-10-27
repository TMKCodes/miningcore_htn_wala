using System;
using System.IO;
using System.Text;
using NBitcoin;
using Miningcore.Extensions;

namespace Miningcore.Blockchain.Scash
{
    public class ScashStream
    {
        private readonly BinaryReader reader;
        private readonly BinaryWriter writer;
        private readonly bool serializing;

        public ScashStream(Stream stream, bool serializing)
        {
            if (serializing)
            {
                writer = new BinaryWriter(stream, Encoding.UTF8, true);
            }
            else
            {
                reader = new BinaryReader(stream, Encoding.UTF8, true);
            }

            this.serializing = serializing;
        }

        /// <summary>
        /// Lecture/écriture d'un tableau de bytes
        /// </summary>
        public void ReadWrite(byte[] data)
        {
            if (serializing)
                writer.Write(data);
            else
                reader.Read(data, 0, data.Length);
        }

        /// <summary>
        /// Lecture/écriture d'un entier non signé (uint32)
        /// </summary>
        public void ReadWrite(ref uint value)
        {
            if (serializing)
                writer.Write(value);
            else
                value = reader.ReadUInt32();
        }

        /// <summary>
        /// Lecture/écriture d'un entier non signé 64 bits (ulong)
        /// </summary>
        public void ReadWrite(ref ulong value)
        {
            if (serializing)
                writer.Write(value);
            else
                value = reader.ReadUInt64();
        }

        /// <summary>
        /// Lecture/écriture d'un entier signé (int64)
        /// </summary>
        public void ReadWrite(ref long value)
        {
            if (serializing)
                writer.Write(value);
            else
                value = reader.ReadInt64();
        }

        /// <summary>
        /// Lecture/écriture d'une chaîne de caractères variable
        /// </summary>
        public void ReadWriteAsVarString(ref byte[] data)
        {
            if (serializing)
            {
                writer.Write7BitEncodedInt(data.Length);
                writer.Write(data);
            }
            else
            {
                int length = reader.Read7BitEncodedInt();
                data = reader.ReadBytes(length);
            }
        }

        /// <summary>
        /// Lecture/écriture d'un tableau de bytes avec longueur variable
        /// </summary>
        public void ReadWriteAsVarInt(ref uint value)
        {
            if (serializing)
            {
                writer.Write7BitEncodedInt((int)value);
            }
            else
            {
                value = (uint)reader.Read7BitEncodedInt();
            }
        }

        /// <summary>
        /// Lecture/écriture d'un `uint256` (NBitcoin)
        /// </summary>
        public void ReadWrite(ref uint256 value)
        {
            if (serializing)
            {
                writer.Write(value.ToBytes());
            }
            else
            {
                value = new uint256(reader.ReadBytes(32));
            }
        }

        /// <summary>
        /// Fermeture du flux
        /// </summary>
        public void Close()
        {
            writer?.Close();
            reader?.Close();
        }
    }
}
