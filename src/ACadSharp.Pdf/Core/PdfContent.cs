using ACadSharp.Entities;
using ACadSharp.Objects;
using ACadSharp.Pdf.Core.IO;
using ACadSharp.Pdf.Core.Render.SceneGraph;
using ACadSharp.Pdf.Extensions;
using CSMath;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ACadSharp.Pdf.Core
{
	public class PdfContent : PdfDictionary
	{
		private static readonly Encoding _contentEncoding = Encoding.GetEncoding(28591);

		public XY Translation { get; set; } = XY.Zero;

		public PdfPage Owner { get; }

		public Layout Layout { get { return this.Owner.Layout; } }

		private readonly StringBuilder _sb = new();
		private long _streamLength;

		public PdfContent(PdfPage owner)
		{
			this.Owner = owner;

			this.Items.Add("/Length", new PdfReference<long>(() => this._streamLength));
		}

		public override string GetPdfForm(PdfConfiguration configuration)
		{
			string streamText = this.getContentStreamText(configuration);
			this._streamLength = _contentEncoding.GetByteCount(streamText);
			this.Items.Remove("/Filter");

			StringBuilder str = new StringBuilder();
			str.Append(this.getStartObj());
			str.Append(this.getBody(configuration));
			str.AppendLine(PdfKey.StreamStart);
			str.Append(streamText);
			if (streamText.Length > 0 && streamText[streamText.Length - 1] != '\n')
			{
				str.AppendLine();
			}
			str.AppendLine(PdfKey.StreamEnd);
			str.Append(this.getEndObj());

			return str.ToString();
		}

		public byte[] GetPdfObjectBytes(PdfConfiguration configuration)
		{
			byte[] streamData = this.getContentStreamBytes(configuration);
			byte[] payload = configuration.CompressContentStreams ? compress(streamData) : streamData;
			this._streamLength = payload.LongLength;
			if (configuration.CompressContentStreams)
			{
				this.Items["/Filter"] = new PdfName("/FlateDecode");
			}
			else
			{
				this.Items.Remove("/Filter");
			}

			string header = this.getStartObj() + this.getBody(configuration) + PdfKey.StreamStart + "\n";
			string footer = "\n" + PdfKey.StreamEnd + "\n" + this.getEndObj();
			byte[] headerBytes = _contentEncoding.GetBytes(header);
			byte[] footerBytes = _contentEncoding.GetBytes(footer);
			byte[] output = new byte[headerBytes.Length + payload.Length + footerBytes.Length];

			Buffer.BlockCopy(headerBytes, 0, output, 0, headerBytes.Length);
			Buffer.BlockCopy(payload, 0, output, headerBytes.Length, payload.Length);
			Buffer.BlockCopy(footerBytes, 0, output, headerBytes.Length + payload.Length, footerBytes.Length);

			return output;
		}

		private byte[] getContentStreamBytes(PdfConfiguration configuration)
		{
			return _contentEncoding.GetBytes(this.getContentStreamText(configuration));
		}

		private string getContentStreamText(PdfConfiguration configuration)
		{
			this._sb.Clear();

			this.writeStackStart();

			if (configuration.UseSceneGraph)
			{
				var pipeline = new SceneGraphPdfPipeline(this.Layout, configuration);
				string ops = pipeline.Render(this.Owner.Viewports, this.Owner.Entities, this.Owner.ModelEntities, out var log);
				configuration.RegisterRenderLog(this.Owner, log);
				this._sb.Append(ops);
			}
			else
			{
				PdfPen pen = new PdfPen(this.Layout, configuration);

				foreach (Viewport v in this.Owner.Viewports)
				{
					pen.DrawEntity(v);
				}

				foreach (Entity e in this.Owner.Entities)
				{
					pen.DrawEntity(e);
				}

				this._sb.Append(pen.ToString());
			}

			this.writeStackEnd();

			string normalized = normalizeLineEndings(this._sb.ToString());
			this._sb.Clear();
			this._sb.Append(normalized);
			return normalized;
		}

		private void writeStackStart()
		{
			//Stack
			this._sb.AppendLine(PdfKey.StackStart);
			//Bottom left is 0,0

			//translation
			//1 0 0 1 tx ty cm
			//scaling
			//sx 0 0 xy 0 0 cm
			//rotation - clockwise
			//(cos q) (sin q) (-sin q) (cos q) 0 0 cm

			this.getTotalTranslation(out double xt, out double yt);
			this._sb.AppendLine($"1 0 0 1 {xt.ToString(CultureInfo.InvariantCulture)} {yt.ToString(CultureInfo.InvariantCulture)} {PdfKey.CurrentMatrix}");
			this._sb.AppendLine(PdfKey.StackStart);
		}

		private void writeStackEnd()
		{
			//Reset stack
			this._sb.AppendLine("Q");
			this._sb.AppendLine("Q");
		}

		private void getTotalTranslation(out double xt, out double yt)
		{
			xt = PdfUnitType.Millimeter.Transform(this.Layout.UnprintableMargin.Left);
			yt = PdfUnitType.Millimeter.Transform(this.Layout.UnprintableMargin.Bottom);

			xt += PdfUnitType.Millimeter.Transform(this.Translation.X);
			yt += PdfUnitType.Millimeter.Transform(this.Translation.Y);
		}

		private string toPdfDouble(double value)
		{
			return value.ToPdfUnit(this.Layout.PaperUnits).ToString("0.####", CultureInfo.InvariantCulture);
		}

		private static string normalizeLineEndings(string value)
		{
			if (string.IsNullOrEmpty(value))
			{
				return string.Empty;
			}

			return value.Replace("\r\n", "\n").Replace('\r', '\n');
		}

		private static byte[] compress(byte[] data)
		{
			if (data == null || data.Length == 0)
			{
				return Array.Empty<byte>();
			}

			using MemoryStream output = new MemoryStream();
			using (DeflateStream deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
			{
				deflate.Write(data, 0, data.Length);
			}

			return output.ToArray();
		}
	}
}
