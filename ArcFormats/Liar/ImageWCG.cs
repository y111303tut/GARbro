//! \file       ImageWCG.cs
//! \date       Sat Jul 19 23:07:32 2014
//! \brief      Liar-soft WCG image format implementation.
//
// Copyright (C) 2014 by morkt
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to
// deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
// sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
// IN THE SOFTWARE.
//

using System;
using System.IO;
using System.ComponentModel.Composition;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Collections.Generic;
using System.Text;
using GameRes.Utility;

namespace GameRes.Formats.Liar
{
    [Export(typeof(ImageFormat))]
    public class WcgFormat : ImageFormat
    {
        public override string         Tag { get { return "WCG"; } }
        public override string Description { get { return "Liar-soft proprietary image format"; } }
        public override uint     Signature { get { return 0x02714757; } }

        public WcgFormat ()
        {
            Signatures = new uint[] { 0x02714757, 0xF2714757 };
        }

        public override ImageMetaData ReadMetaData (Stream stream)
        {
            if (0x57 != stream.ReadByte() || 0x47 != stream.ReadByte())
                return null;
            using (var file = new BinaryReader (stream, Encoding.ASCII, true))
            {
                uint flags = file.ReadUInt16();
                if (1 != (flags & 0x0f) || 0x20 != file.ReadByte() || 0 != file.ReadByte())
                    return null;
                var meta = new ImageMetaData();
                file.BaseStream.Position = 8;
                meta.Width  = file.ReadUInt32();
                meta.Height = file.ReadUInt32();
                meta.BPP = 32;
                return meta;
            }
        }

        public override ImageData Read (Stream file, ImageMetaData info)
        {
            using (var reader = new Reader (file, info.Width * info.Height))
            {
                reader.Unpack();
                return ImageData.Create (info, PixelFormats.Bgra32, null, reader.Data);
            }
        }

        public override void Write (Stream file, ImageData image)
        {
            Stream stream = file;
            bool buffer_used = false;
            if (!stream.CanSeek)
            {
                stream = new MemoryStream();
                buffer_used = true;
            }
            try
            {
                using (var writer = new Writer (stream, image.Bitmap))
                {
                    writer.Pack();
                }
                if (buffer_used)
                {
                    stream.Position = 0;
                    stream.CopyTo (file);
                }
            }
            finally
            {
                if (buffer_used)
                    stream.Dispose();
            }
        }

        internal class Reader : IDisposable
        {
            private byte[]          m_data;
            private BinaryReader    m_input;
            private uint            m_input_size;
            private ushort[]        m_index;

            private uint m_next_ptr;
            private uint m_next_size;
            private uint m_src;
            private uint m_src_size;
            private uint m_dst_size;
            private uint edi;

            private uint m_index_length_limit;
            private uint m_bits;

            public byte[] Data { get { return m_data; } }

            public Reader (Stream file, uint pixel_size)
            {
                m_data = new byte[pixel_size*4];
                m_input_size = (uint)file.Length;
                m_input = new BinaryReader (file, Encoding.ASCII, true);
            }

            public void Unpack ()
            {
                m_next_ptr = 16;
                m_next_size = m_input_size-16;
                if (Unpack (2))
                    Unpack (0);
                for (uint i = 3; i < m_data.Length; i += 4)
                    m_data[i] = (byte)~m_data[i];
            }

            private bool Unpack (uint offset)
            {
                m_src = m_next_ptr;
                m_src_size = m_next_size;
                m_dst_size = (uint)(m_data.Length / 4);
                if (m_src_size < 12)
                    throw new InvalidFormatException ("Invalid file size");
                m_src_size -= 12;
                m_input.BaseStream.Position = m_next_ptr;
                uint unpacked_size = m_input.ReadUInt32();
                uint data_size = m_input.ReadUInt32();
                uint index_size = m_input.ReadUInt16(); // 8

                if (unpacked_size != m_dst_size*2)
                    throw new InvalidFormatException ("Invalid image size");

                if (0 == index_size || index_size*2 > m_src_size)
                    throw new InvalidFormatException ("Invalid palette size");

                m_src_size -= index_size*2;
                if (data_size > m_src_size)
                    throw new InvalidFormatException ("Invalid compressed data size");

                uint data_pos = m_src + index_size*2 + 12;
                edi = offset;
                m_next_size = m_src_size - data_size;
                m_next_ptr = data_pos + data_size;
                m_src_size = data_size;
                return DecodeStream (data_pos, index_size);
            }

            void ReadIndex (uint index_size)
            {
                m_input.BaseStream.Position = m_src+12;
                m_index = new ushort[index_size];
                for (int i = 0; i < index_size; ++i)
                    m_index[i] = m_input.ReadUInt16();
            }

            bool DecodeStream (uint data_pos, uint index_size)
            {
                ReadIndex (index_size);
                m_input.BaseStream.Position = data_pos;

                bool small_index = index_size < 0x1002;
                m_index_length_limit = small_index ? 0x06u : 0x0eu;
                uint index_bit_length = small_index ? 3u : 4u;
                m_bits = 0;
                while (m_dst_size > 0)
                {
                    uint dst_count = 1;
                    uint index_length = GetBits (index_bit_length, 0);
                    if (0 == index_length)
                    {
                        dst_count = GetBits (4, 0) + 2;
                        index_length = GetBits (index_bit_length, 0);
                    }
                    if (0 == index_length)
                        return false; // std::cerr << "zero index length\n";

                    uint index = GetIndex (index_length);
                    if (index >= index_size)
                        return false; // std::cerr << "invalid index\n";

                    if (dst_count > m_dst_size)
                        return false;
                    m_dst_size -= dst_count;
                    ushort word = m_index[index];
                    do {
                        PutWord (word); // *(uint16_t*)edi = word;
                        edi += 4;
                    } while (0 != --dst_count);
                }
                return true;
            }

            void PutWord (ushort word)
            {
                m_data[edi  ] = (byte)(word & 0xff);
                m_data[edi+1] = (byte)(word >> 8);
            }

            uint GetNextBit ()
            {
                m_bits <<= 1;
                if (0 == (m_bits & 0xff))
                {
                    if (0 == m_src_size--)
                        throw new InvalidFormatException ("Unexpected end of file");
                    m_bits = (uint)m_input.ReadByte() << 1;
                    m_bits |= 1;
                }
                return (m_bits >> 8) & 1;
            }

            uint GetIndex (uint count)
            {
                if (0 == --count)
                    return GetNextBit();
                if (count < m_index_length_limit)
                    return GetBits (count, 1);
                while (0 != GetNextBit())
                {
                    if (count >= 0x10)
                        throw new InvalidFormatException ("Invalid index count");
                    ++count;
                }
                return GetBits (count, 1);
            }

            uint GetBits (uint count, uint word)
            {
                do
                {
                    word = (word << 1) | GetNextBit();
                }
                while (0 != --count);
                return word;
            }

            #region IDisposable Members
            bool disposed = false;

            public void Dispose ()
            {
                Dispose (true);
                GC.SuppressFinalize (this);
            }

            protected virtual void Dispose (bool disposing)
            {
                if (!disposed)
                {
                    if (disposing)
                        m_input.Dispose();
                    m_input = null;
                    m_data = null;
                    m_index = null;
                    disposed = true;
                }
            }
            #endregion
        }

        private class Writer : IDisposable
        {
            private BinaryWriter    m_out;
            private uint            m_width;
            private uint            m_height;
            private uint            m_pixels;
            private byte[]          m_data;

            Dictionary<ushort, ushort> m_index = new Dictionary<ushort, ushort>();
            private uint m_base_length;
            private uint m_base_index_length;
            private int  m_bits;

            public Writer (Stream stream, BitmapSource bitmap)
            {
                m_width  = (uint)bitmap.PixelWidth;
                m_height = (uint)bitmap.PixelHeight;
                m_pixels = m_width*m_height;
                if (bitmap.Format != PixelFormats.Bgra32)
                {
                    var converted_bitmap = new FormatConvertedBitmap();
                    converted_bitmap.BeginInit();
                    converted_bitmap.Source = bitmap;
                    converted_bitmap.DestinationFormat = PixelFormats.Bgra32;
                    converted_bitmap.EndInit();
                    bitmap = converted_bitmap;
                }
                m_data = new byte[m_pixels*4];
                bitmap.CopyPixels (m_data, bitmap.PixelWidth*4, 0);
                m_out = new BinaryWriter (stream, Encoding.ASCII, true);
            }

            public void Pack ()
            {
                byte[] header = { (byte)'W', (byte)'G', 0x71, 2, 0x20, 0, 0, 0x40 };
                m_out.Write (header, 0, header.Length);
                m_out.Write (m_width);
                m_out.Write (m_height);

                Pack (1, 0xff00);
                Pack (0, 0);
            }

            ushort GetWord (int offset)
            {
                return (ushort)(m_data[offset*2] | m_data[offset*2+1] << 8);
            }

            private void Pack (int data, ushort mask)
            {
                var header_pos = m_out.Seek (0, SeekOrigin.Current);
                m_out.Seek (12, SeekOrigin.Current);

                BuildIndex (data, mask);
                bool small_index = m_index.Count < 0x1002;
                m_base_length = small_index ? 3u : 4u;
                m_base_index_length = small_index ? 7u : 15u;
                m_bits = 1;

                // encode
                for (uint i = 0; i < m_pixels;)
                {
                    ushort word = GetWord (data);
                    data += 2;
                    ++i;
                    ushort color = m_index[(ushort)(word^mask)];
                    uint count = 1;
                    while (i < m_pixels)
                    {
                        if (word != GetWord (data))
                            break;
                        ++count;
                        data += 2;
                        ++i;
                        if (0x11 == count)
                            break;
                    }
                    if (count > 1)
                    {
                        PutBits (m_base_length, 0);
                        PutBits (4, count-2);
                    }
                    PutIndex (color);
                }
                Flush();

                var end_pos = m_out.Seek (0, SeekOrigin.Current);
                uint data_size = (uint)(end_pos - header_pos - 12 - m_index.Count*2);
                m_out.Seek ((int)header_pos, SeekOrigin.Begin);
                m_out.Write (m_pixels*2u);
                m_out.Write (data_size);
                m_out.Write ((ushort)m_index.Count);
                m_out.Write ((ushort)(small_index ? 7 : 14)); // 0x0e
                m_out.Seek ((int)end_pos, SeekOrigin.Begin);
            }

            void BuildIndex (int data, ushort mask)
            {
                m_index.Clear();
                uint[] freq_table = new uint[65536];
                for (var data_end = data + m_pixels*2; data < data_end; data += 2)
                    freq_table[GetWord (data)^mask]++;

                var index = new List<ushort>();
                for (int i = 0; i < freq_table.Length; ++i)
                {
                    if (0 != freq_table[i])
                        index.Add ((ushort)i);
                }
                index.Sort ((a, b) => freq_table[a] < freq_table[b] ? 1 : freq_table[a] == freq_table[b] ? 0 : -1);
                ushort j = 0;
                foreach (var color in index)
                {
                    m_out.Write (color);
                    m_index.Add (color, j++);
                }
            }

            void Flush ()
            {
                if (1 != m_bits)
                {
                    do
                        m_bits <<= 1;
                    while (0 == (m_bits & 0x100));
                    m_out.Write ((byte)(m_bits & 0xff));
                    m_bits = 1;
                }
            }

            void PutBit (bool bit)
            {
                m_bits <<= 1;
                m_bits |= bit ? 1 : 0;
                if (0 != (m_bits & 0x100))
                {
                    m_out.Write ((byte)(m_bits & 0xff));
                    m_bits = 1;
                }
            }

            void PutBits (uint length, uint x)
            {
                x <<= (int)(32-length);
                while (0 != length--)
                {
                    PutBit (0 != (x & 0x80000000));
                    x <<= 1;
                }
            }

            static uint GetBitsLength (ushort val)
            {
                uint length = 0;
                do
                {
                    ++length;
                    val >>= 1;
                }
                while (0 != val);
                return length;
            }

            void PutIndex (ushort index)
            {
                uint length = GetBitsLength (index);

                if (length < m_base_index_length)
                {
                    PutBits (m_base_length, length);
                    if (1 == length)
                        PutBit (index != 0);
                    else
                        PutBits (length-1, index);
                }
                else
                {
                    PutBits (m_base_length, m_base_index_length);
                    for (uint i = m_base_index_length; i < length; ++i)
                        PutBit (true);
                    PutBit (false);
                    PutBits (length-1, index);
                }
            }

            #region IDisposable Members
            bool disposed = false;

            public void Dispose ()
            {
                Dispose (true);
                GC.SuppressFinalize (this);
            }

            protected virtual void Dispose (bool disposing)
            {
                if (!disposed)
                {
                    if (disposing)
                        m_out.Dispose();
                    m_out = null;
                    m_data = null;
                    m_index = null;
                    disposed = true;
                }
            }
            #endregion
        }
    }

    [Export(typeof(ImageFormat))]
    public class LimFormat : ImageFormat
    {
        public override string         Tag { get { return "LIM"; } }
        public override string Description { get { return "Liar-soft image format"; } }
        public override uint     Signature { get { return 0; } }

        public override ImageMetaData ReadMetaData (Stream stream)
        {
            if (0x4C != stream.ReadByte() || 0x4D != stream.ReadByte())
                return null;
            int flag = stream.ReadByte() & 0xF;
            if (flag != 2 && flag != 3)
                return null;
            stream.Seek (5, SeekOrigin.Current);
            using (var file = new ArcView.Reader (stream))
            {
                var meta = new ImageMetaData { BPP = 32 };
                meta.Width  = file.ReadUInt32();
                meta.Height = file.ReadUInt32();
                return meta;
            }
        }

        public override ImageData Read (Stream file, ImageMetaData info)
        {
            file.Position = 0x10;
            using (var reader = new Reader (file, info))
            {
                reader.Unpack();
                return ImageData.Create (info, reader.Format, null, reader.Data);
            }
        }

        public override void Write (Stream file, ImageData image)
        {
            throw new NotImplementedException ("LimFormat.Write not implemented");
        }

        internal class Reader : IDisposable
        {
            BinaryReader    m_input;
            byte[]          m_output;
            byte[]          m_index;
            byte[]          m_image;
            int             m_width;
            int             m_height;

            public byte[]        Data { get { return m_image; } }
            public PixelFormat Format { get { return PixelFormats.Bgra32; } }

            public Reader (Stream file, ImageMetaData info)
            {
                m_input = new ArcView.Reader (file);
                m_width = (int)info.Width;
                m_height = (int)info.Height;
                m_image = new byte[m_width*m_height*4];
            }

            int         m_remaining;
            int         m_current;
            int         m_bits;

            public void Unpack ()
            {
                byte mask = 0xFF;
                for (int i = 3; i >= 0; --i)
                {
                    int channel_size = m_input.ReadInt32();
                    if (null == m_output)
                        m_output = new byte[channel_size];
                    else if (channel_size != m_output.Length)
                        throw new InvalidFormatException();

                    m_remaining = m_input.ReadInt32();
                    int index_size = m_input.ReadUInt16();
                    if (null == m_index || index_size > m_index.Length)
                        m_index = new byte[index_size];
                    m_input.ReadInt16(); // ignored
                    if (m_index.Length != m_input.Read (m_index, 0, m_index.Length))
                        throw new InvalidFormatException ("Unexpected end of file");

                    UnpackChannel();
                    int src = 0;
                    for (int p = i; p < m_image.Length; p += 4)
                    {
                        m_image[p] = (byte)(m_output[src++] ^ mask);
                    }
                    mask = 0;
                }
            }

            void UnpackChannel ()
            {
                m_current = 0;
                int dst = 0;
                while (dst < m_output.Length)
                {
                    int bits = GetBits (3);
                    if (-1 == bits)
                        break;

                    if (0 != bits)
                    {
                        int index = GetIndex (bits);
                        if (index < 0)
                            break;
                        if (dst + 1 >= m_output.Length)
                            break;

                        m_output[dst++] = m_index[index];
                    }
                    else
                    {
                        int count = GetBits (4);
                        if (-1 == count)
                            break;

                        bits = GetBits (3);
                        if (-1 == bits)
                            break;

                        int index = GetIndex (bits);
                        if (-1 == index)
                            break;
                        count += 2;
                        for (int i = 0; i < count; i++)
                        {
                            if (dst + 1 >= m_output.Length)
                                return;
                            m_output[dst++] = m_index[index];
                        }
                    }
                }
            }

            private int GetBits (int n)
            {
                int v = 0;
                while (n > 0)
                {
                    if (0 == m_current)
                    {
                        m_bits = m_input.ReadByte();
                        m_current = 8;
                    }
                    v <<= 1;
                    m_bits <<= 1;
                    v |= (m_bits >> 8) & 1;
                    --m_current;
                    --n;
                }
                return v;
            }

            private int GetIndex (int bits)
            {
                if (bits < 7)
                {
                    if (0 == bits)
                        return -1;
                    if (1 == bits--)
                        return GetBits (1);
                    return (1 << bits) | GetBits (bits);
                }
                for (int i = 6; i < 12; ++i)
                {
                    bits = GetBits (1);
                    if (-1 == bits)
                        return -1;
                    if (0 == bits)
                        return (1 << i) | GetBits (i);
                }
                return -1;
            }

            #region IDisposable Members
            bool disposed = false;

            public void Dispose ()
            {
                Dispose (true);
                GC.SuppressFinalize (this);
            }

            protected virtual void Dispose (bool disposing)
            {
                if (!disposed)
                {
                    if (disposing)
                        m_input.Dispose();
                }
            }
            #endregion
        }
    }
}