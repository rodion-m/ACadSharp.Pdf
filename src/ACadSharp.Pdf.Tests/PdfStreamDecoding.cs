using System;
using System.IO;
using System.IO.Compression;

namespace ACadSharp.Pdf.Tests
{
	internal static class PdfStreamDecoding
	{
		public static byte[] DecodeFlatePayload(byte[] compressed)
		{
			if (compressed == null)
			{
				throw new ArgumentNullException(nameof(compressed));
			}

			if (compressed.Length < 6)
			{
				throw new InvalidDataException("Flate stream is too short to contain a zlib wrapper.");
			}

			int header = (compressed[0] << 8) | compressed[1];
			if ((header % 31) != 0)
			{
				throw new InvalidDataException("Flate stream has an invalid zlib header.");
			}

			int deflateLength = compressed.Length - 6;
			using var input = new MemoryStream(compressed, 2, deflateLength, writable: false);
			using var deflate = new DeflateStream(input, CompressionMode.Decompress);
			using var output = new MemoryStream();
			deflate.CopyTo(output);

			byte[] decoded = output.ToArray();
			uint expected = readUInt32BigEndian(compressed, compressed.Length - 4);
			uint actual = computeAdler32(decoded);
			if (expected != actual)
			{
				throw new InvalidDataException("Flate stream Adler-32 checksum mismatch.");
			}

			return decoded;
		}

		private static uint computeAdler32(byte[] data)
		{
			const uint mod = 65521;
			uint a = 1;
			uint b = 0;

			foreach (byte item in data)
			{
				a = (a + item) % mod;
				b = (b + a) % mod;
			}

			return (b << 16) | a;
		}

		private static uint readUInt32BigEndian(byte[] buffer, int offset)
		{
			return ((uint)buffer[offset] << 24)
				| ((uint)buffer[offset + 1] << 16)
				| ((uint)buffer[offset + 2] << 8)
				| buffer[offset + 3];
		}
	}
}
