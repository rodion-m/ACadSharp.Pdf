using ACadSharp.Pdf.Core.Render;

namespace ACadSharp.Pdf
{
	public sealed class PageRenderLog
	{
		public string LayoutName { get; }

		public RenderLog Log { get; }

		public PageRenderLog(string layoutName, RenderLog log)
		{
			this.LayoutName = string.IsNullOrWhiteSpace(layoutName) ? "Unnamed" : layoutName;
			this.Log = log;
		}
	}
}
