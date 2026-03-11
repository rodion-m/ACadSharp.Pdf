using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Pdf.Core;
using ACadSharp.Objects;
using ACadSharp.Pdf.Core.Render;
using CSMath;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ACadSharp.Pdf.Examples
{
	internal class Program
	{
		static void Main(string[] args)
		{
			Options options = Options.Parse(args);
			var notifications = new NotificationCollector();
			CadDocument doc = loadDocument(options.InputPath, notifications);

			string outputPdf = options.OutputPdfPath;
			Directory.CreateDirectory(Path.GetDirectoryName(outputPdf) ?? ".");

			PdfExporter exporter = new PdfExporter(outputPdf);
			exporter.Configuration.UseSceneGraph = options.UseSceneGraph;
			exporter.Configuration.BasePath = Path.GetDirectoryName(options.InputPath) ?? string.Empty;
			exporter.Configuration.OnNotification += notifications.OnNotification;

			PreviewLayoutPlan previewPlan = null;
			if (options.ExportModelSpace)
			{
				previewPlan = addModelSpace(exporter, doc, options);
			}

			if (options.ExportPaperLayouts)
			{
				exporter.AddPaperLayouts(doc);
			}

			exporter.Close();

			string reportPath = options.ReportPath ?? Path.ChangeExtension(outputPdf, ".render-report.json");
			writeReport(reportPath, doc, options, previewPlan, exporter.Configuration.PageRenderLogs, notifications);
			notifications.FlushToConsole();

			Console.WriteLine($"PDF: {outputPdf}");
			Console.WriteLine($"Report: {reportPath}");
		}

		private static PreviewLayoutPlan addModelSpace(PdfExporter exporter, CadDocument doc, Options options)
		{
			if (!options.FitModelToPaper)
			{
				exporter.AddModelSpace(doc);
				return null;
			}

			BoundingBox limits;
			if (options.TryGetWindow(out BoundingBox explicitWindow))
			{
				limits = explicitWindow;
			}
			else if (!tryComputeExtents(doc.ModelSpace.Entities, options.FocusHandles, options.FocusPaddingModelUnits, out limits))
			{
				exporter.AddModelSpace(doc);
				return null;
			}

			var layout = new Layout("ModelPreview")
			{
				PaperUnits = PlotPaperUnits.Millimeters,
				PaperWidth = options.PaperWidthMm,
				PaperHeight = options.PaperHeightMm,
				DenominatorScale = 1.0,
				PaperRotation = PlotRotation.NoRotation,
			};

			PdfPage page = exporter.AddModelWindow(doc, layout, limits, options.MarginMm);
			Viewport previewViewport = page.Viewports.Single();

			return new PreviewLayoutPlan
			{
				PaperWidthMm = options.PaperWidthMm,
				PaperHeightMm = options.PaperHeightMm,
				MarginMm = options.MarginMm,
				FocusHandles = options.FocusHandles.ToArray(),
				FocusPaddingModelUnits = options.FocusPaddingModelUnits,
				DenominatorScale = previewViewport.ViewHeight / previewViewport.Height,
				MinX = limits.Min.X,
				MinY = limits.Min.Y,
				MaxX = limits.Max.X,
				MaxY = limits.Max.Y,
				TranslationX = 0.0,
				TranslationY = 0.0,
				ViewportWidth = previewViewport.Width,
				ViewportHeight = previewViewport.Height,
				ViewHeight = previewViewport.ViewHeight,
			};
		}

		private static bool tryComputeExtents(IEnumerable<Entity> entities, IReadOnlyCollection<string> focusHandles, double focusPaddingModelUnits, out BoundingBox limits)
		{
			limits = BoundingBox.Null;
			HashSet<string> normalizedHandles = new HashSet<string>(
				(focusHandles ?? Array.Empty<string>())
					.Where(h => !string.IsNullOrWhiteSpace(h))
					.Select(h => h.Trim().ToUpperInvariant()));

			foreach (Entity entity in entities ?? Array.Empty<Entity>())
			{
				if (entity == null)
				{
					continue;
				}

				if (normalizedHandles.Count > 0)
				{
					string handle = entity.Handle.ToString("X");
					if (!normalizedHandles.Contains(handle))
					{
						continue;
					}
				}

				BoundingBox box;
				try
				{
					box = entity.GetBoundingBox();
				}
				catch
				{
					continue;
				}

				if (box.Extent != BoundingBoxExtent.Finite && box.Extent != BoundingBoxExtent.Point)
				{
					continue;
				}

				if (!isFinite(box.Min.X) || !isFinite(box.Min.Y) || !isFinite(box.Max.X) || !isFinite(box.Max.Y))
				{
					continue;
				}

				limits = limits.Merge(box);
			}

			if (limits.Extent != BoundingBoxExtent.Null && focusPaddingModelUnits > 0.0)
			{
				limits = new BoundingBox(
					new XYZ(limits.Min.X - focusPaddingModelUnits, limits.Min.Y - focusPaddingModelUnits, limits.Min.Z),
					new XYZ(limits.Max.X + focusPaddingModelUnits, limits.Max.Y + focusPaddingModelUnits, limits.Max.Z));
			}

			return limits.Extent != BoundingBoxExtent.Null;
		}

		private static bool isFinite(double value)
		{
			return !double.IsNaN(value) && !double.IsInfinity(value);
		}

		private static CadDocument loadDocument(string path, NotificationCollector notifications)
		{
			string ext = Path.GetExtension(path).ToLowerInvariant();
			if (ext == ".dxf")
			{
				return DxfReader.Read(path);
			}

			ACadSharp.IO.NotificationEventHandler handler = notifications != null ? new ACadSharp.IO.NotificationEventHandler(notifications.OnNotification) : null;
			return DwgReader.Read(path, handler);
		}

		private static void writeReport(string reportPath, CadDocument doc, Options options, PreviewLayoutPlan previewPlan, IEnumerable<PageRenderLog> pageRenderLogs, NotificationCollector notifications)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");

			var modelCounts = doc.ModelSpace.Entities
				.GroupBy(e => e.GetType().Name)
				.OrderByDescending(g => g.Count())
				.ToDictionary(g => g.Key, g => g.Count());

			var layoutSummaries = doc.Layouts
				.OrderBy(l => l.Name)
				.Select(l => new
				{
					l.Name,
					l.IsPaperSpace,
					EntityCount = l.AssociatedBlock?.Entities?.Count() ?? 0,
					ViewportCount = l.Viewports?.Count() ?? 0,
				})
				.ToList();

			var renderEntries = (pageRenderLogs ?? Array.Empty<PageRenderLog>())
				.SelectMany((pageLog, pageIndex) => (pageLog?.Log?.Entries ?? Array.Empty<RenderLogEntry>())
					.Select(e => new
					{
						pageIndex,
						layout = pageLog?.LayoutName,
						handle = e.Handle.ToString("X"),
						entityType = e.EntityType,
						status = e.Status.ToString(),
						reason = e.Reason,
					}))
				.ToList();

			var renderStatusCounts = renderEntries
				.GroupBy(e => e.status)
				.OrderBy(g => g.Key)
				.ToDictionary(g => g.Key, g => g.Count());

			var renderTypeCounts = renderEntries
				.GroupBy(e => e.entityType)
				.OrderByDescending(g => g.Count())
				.ToDictionary(g => g.Key, g => g.Count());

			var renderPageSummaries = (pageRenderLogs ?? Array.Empty<PageRenderLog>())
				.Select((pageLog, pageIndex) => new
				{
					pageIndex,
					layout = pageLog?.LayoutName,
					entryCount = pageLog?.Log?.Entries?.Count ?? 0,
					statusCounts = (pageLog?.Log?.Entries ?? Array.Empty<RenderLogEntry>())
						.GroupBy(e => e.Status.ToString())
						.OrderBy(g => g.Key)
						.ToDictionary(g => g.Key, g => g.Count()),
				})
				.ToList();

			var report = new
			{
				input = options.InputPath,
				outputPdf = options.OutputPdfPath,
				sceneGraph = options.UseSceneGraph,
				exportModelSpace = options.ExportModelSpace,
				exportPaperLayouts = options.ExportPaperLayouts,
				fitModelToPaper = options.FitModelToPaper,
				previewLayout = previewPlan,
				document = new
				{
					layoutCount = doc.Layouts.Count(),
					paperLayoutCount = doc.Layouts.Count(l => l.IsPaperSpace),
					modelEntityCount = doc.ModelSpace.Entities.Count(),
					modelEntityTypes = modelCounts,
					layouts = layoutSummaries,
				},
				renderLog = new
				{
					entryCount = renderEntries.Count,
					statusCounts = renderStatusCounts,
					entityTypeCounts = renderTypeCounts,
					pages = renderPageSummaries,
					entries = renderEntries,
				},
				notifications = notifications?.CreateSummary(),
			};

			var jsonOptions = new JsonSerializerOptions
			{
				WriteIndented = true,
			};
			File.WriteAllText(reportPath, JsonSerializer.Serialize(report, jsonOptions));
		}

		private sealed class PreviewLayoutPlan
		{
			public double PaperWidthMm { get; set; }
			public double PaperHeightMm { get; set; }
			public double MarginMm { get; set; }
			public string[] FocusHandles { get; set; }
			public double FocusPaddingModelUnits { get; set; }
			public double DenominatorScale { get; set; }
			public double MinX { get; set; }
			public double MinY { get; set; }
			public double MaxX { get; set; }
			public double MaxY { get; set; }
			public double TranslationX { get; set; }
			public double TranslationY { get; set; }
			public double ViewportWidth { get; set; }
			public double ViewportHeight { get; set; }
			public double ViewHeight { get; set; }
		}

		private sealed class Options
		{
			public string InputPath { get; private set; }
			public string OutputPdfPath { get; private set; }
			public string ReportPath { get; private set; }
			public bool UseSceneGraph { get; private set; } = true;
			public bool ExportModelSpace { get; private set; } = true;
			public bool ExportPaperLayouts { get; private set; }
			public bool FitModelToPaper { get; private set; } = true;
			public double PaperWidthMm { get; private set; } = 1189.0;
			public double PaperHeightMm { get; private set; } = 841.0;
			public double MarginMm { get; private set; } = 10.0;
			public List<string> FocusHandles { get; } = new List<string>();
			public double FocusPaddingModelUnits { get; private set; } = 200.0;
			public BoundingBox? ExplicitWindow { get; private set; }

			public static Options Parse(IReadOnlyList<string> args)
			{
				var options = new Options();

				for (int i = 0; i < args.Count; i++)
				{
					string arg = args[i];
					switch (arg)
					{
						case "--input":
							options.InputPath = requireValue(args, ref i, arg);
							break;
						case "--output":
							options.OutputPdfPath = requireValue(args, ref i, arg);
							break;
						case "--report":
							options.ReportPath = requireValue(args, ref i, arg);
							break;
						case "--pipeline":
							string pipeline = requireValue(args, ref i, arg);
							options.UseSceneGraph = !string.Equals(pipeline, "legacy", StringComparison.OrdinalIgnoreCase);
							break;
						case "--mode":
							applyMode(options, requireValue(args, ref i, arg));
							break;
						case "--full-scale":
							options.FitModelToPaper = false;
							break;
						case "--paper-width-mm":
							options.PaperWidthMm = double.Parse(requireValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture);
							break;
						case "--paper-height-mm":
							options.PaperHeightMm = double.Parse(requireValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture);
							break;
						case "--margin-mm":
							options.MarginMm = double.Parse(requireValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture);
							break;
						case "--focus-handle":
							options.FocusHandles.Add(requireValue(args, ref i, arg));
							break;
						case "--focus-padding-model":
							options.FocusPaddingModelUnits = double.Parse(requireValue(args, ref i, arg), System.Globalization.CultureInfo.InvariantCulture);
							break;
						case "--window":
							options.ExplicitWindow = parseWindow(args, ref i);
							break;
						case "--help":
						case "-h":
						case "/?":
							printUsageAndExit(0);
							break;
						default:
							throw new ArgumentException($"Unknown argument: {arg}");
					}
				}

				if (string.IsNullOrWhiteSpace(options.InputPath))
				{
					printUsageAndExit(1);
				}

				options.InputPath = Path.GetFullPath(options.InputPath);
				options.OutputPdfPath ??= Path.ChangeExtension(options.InputPath, options.UseSceneGraph ? ".scenegraph.pdf" : ".legacy.pdf");
				options.OutputPdfPath = Path.GetFullPath(options.OutputPdfPath);
				if (!string.IsNullOrWhiteSpace(options.ReportPath))
				{
					options.ReportPath = Path.GetFullPath(options.ReportPath);
				}

				return options;
			}

			public bool TryGetWindow(out BoundingBox window)
			{
				if (this.ExplicitWindow.HasValue)
				{
					window = this.ExplicitWindow.Value;
					return true;
				}

				window = BoundingBox.Null;
				return false;
			}

			private static void applyMode(Options options, string mode)
			{
				switch ((mode ?? string.Empty).Trim().ToLowerInvariant())
				{
					case "model":
						options.ExportModelSpace = true;
						options.ExportPaperLayouts = false;
						break;
					case "layouts":
						options.ExportModelSpace = false;
						options.ExportPaperLayouts = true;
						break;
					case "both":
						options.ExportModelSpace = true;
						options.ExportPaperLayouts = true;
						break;
					default:
						throw new ArgumentException($"Unknown mode: {mode}");
				}
			}

			private static string requireValue(IReadOnlyList<string> args, ref int index, string arg)
			{
				if (index + 1 >= args.Count)
				{
					throw new ArgumentException($"Missing value for {arg}");
				}

				index += 1;
				return args[index];
			}

			private static BoundingBox parseWindow(IReadOnlyList<string> args, ref int index)
			{
				double minX = double.Parse(requireValue(args, ref index, "--window"), System.Globalization.CultureInfo.InvariantCulture);
				double minY = double.Parse(requireValue(args, ref index, "--window"), System.Globalization.CultureInfo.InvariantCulture);
				double maxX = double.Parse(requireValue(args, ref index, "--window"), System.Globalization.CultureInfo.InvariantCulture);
				double maxY = double.Parse(requireValue(args, ref index, "--window"), System.Globalization.CultureInfo.InvariantCulture);
				return new BoundingBox(
					new XYZ(Math.Min(minX, maxX), Math.Min(minY, maxY), 0.0),
					new XYZ(Math.Max(minX, maxX), Math.Max(minY, maxY), 0.0));
			}

			private static void printUsageAndExit(int code)
			{
				Console.WriteLine("Usage:");
				Console.WriteLine("  ACadSharp.Pdf.Examples --input <file.dwg|file.dxf> [--output <file.pdf>] [--report <file.json>] [--pipeline scenegraph|legacy] [--mode model|layouts|both] [--full-scale] [--paper-width-mm <n>] [--paper-height-mm <n>] [--margin-mm <n>] [--focus-handle <HEX>] [--focus-padding-model <n>] [--window <minX> <minY> <maxX> <maxY>]");
				Environment.Exit(code);
			}
		}
	}
}
