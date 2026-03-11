using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Pdf.Verification;
using ACadSharp.Tables;
using CSMath;
using System;
using Xunit;

namespace ACadSharp.Pdf.Tests
{
	public class PreviewExtentsSelectorTests
	{
		[Fact]
		public void TrySelect_RejectsFarAwayOutlierCluster()
		{
			CadDocument doc = new CadDocument();
			doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0) });
			doc.Entities.Add(new Line { StartPoint = new XYZ(10, 10, 0), EndPoint = new XYZ(80, 40, 0) });
			doc.Entities.Add(new Circle { Center = new XYZ(30, 20, 0), Radius = 10 });
			var outlier = new Line { StartPoint = new XYZ(15000, -10000, 0), EndPoint = new XYZ(15100, -10000, 0) };
			doc.Entities.Add(outlier);

			bool selected = PreviewExtentsSelector.TrySelect(
				doc.ModelSpace.Entities,
				Array.Empty<string>(),
				paddingModelUnits: 0.0,
				out PreviewExtentsSelection selection);

			Assert.True(selected);
			Assert.Equal("clustered-main-component", selection.Strategy);
			Assert.Equal(4, selection.CandidateCount);
			Assert.Equal(3, selection.IncludedCount);
			Assert.Equal(1, selection.ClusterCount);
			Assert.Single(selection.ExcludedEntities);
			Assert.Equal(outlier.Handle.ToString("X"), selection.ExcludedEntities[0].Handle);
			Assert.Equal("outlier-cluster", selection.ExcludedEntities[0].Reason);
			Assert.True(selection.Limits.Max.X < 1000.0);
			Assert.True(selection.Limits.Min.Y > -1000.0);
		}

		[Fact]
		public void TrySelect_FocusHandlesRemainDeterministic()
		{
			CadDocument doc = new CadDocument();
			doc.Entities.Add(new Line { StartPoint = new XYZ(0, 0, 0), EndPoint = new XYZ(100, 0, 0) });
			doc.Entities.Add(new Line { StartPoint = new XYZ(10, 10, 0), EndPoint = new XYZ(80, 40, 0) });
			var focused = new Line { StartPoint = new XYZ(15000, -10000, 0), EndPoint = new XYZ(15100, -10000, 0) };
			doc.Entities.Add(focused);

			bool selected = PreviewExtentsSelector.TrySelect(
				doc.ModelSpace.Entities,
				new[] { focused.Handle.ToString("X") },
				paddingModelUnits: 25.0,
				out PreviewExtentsSelection selection);

			Assert.True(selected);
			Assert.Equal("focused-handles", selection.Strategy);
			Assert.Equal(1, selection.CandidateCount);
			Assert.Equal(1, selection.IncludedCount);
			Assert.Equal(2, selection.FilteredByHandleCount);
			Assert.Empty(selection.ExcludedEntities);
			Assert.Equal(14975.0, selection.Limits.Min.X, 6);
			Assert.Equal(-10025.0, selection.Limits.Min.Y, 6);
			Assert.Equal(15125.0, selection.Limits.Max.X, 6);
			Assert.Equal(-9975.0, selection.Limits.Max.Y, 6);
		}

		[Fact]
		public void TrySelect_PrunesOversizedInsertThatBloatsClusterExtents()
		{
			CadDocument doc = new CadDocument();
			doc.Entities.Add(new Line { StartPoint = new XYZ(-100, -20, 0), EndPoint = new XYZ(100, -20, 0) });
			doc.Entities.Add(new Line { StartPoint = new XYZ(-80, 40, 0), EndPoint = new XYZ(90, 60, 0) });
			doc.Entities.Add(new Circle { Center = new XYZ(10, 20, 0), Radius = 25.0 });
			doc.Entities.Add(new Line { StartPoint = new XYZ(-120, -60, 0), EndPoint = new XYZ(-120, 80, 0) });
			doc.Entities.Add(new Line { StartPoint = new XYZ(-60, -80, 0), EndPoint = new XYZ(80, -80, 0) });
			doc.Entities.Add(new Line { StartPoint = new XYZ(120, -40, 0), EndPoint = new XYZ(120, 70, 0) });
			doc.Entities.Add(new Line { StartPoint = new XYZ(-30, -90, 0), EndPoint = new XYZ(-30, 90, 0) });
			doc.Entities.Add(new Line { StartPoint = new XYZ(30, -90, 0), EndPoint = new XYZ(30, 90, 0) });

			var block = new BlockRecord("GIANT-CONTEXT");
			block.Entities.Add(new LwPolyline(new[]
			{
				new XY(-2500, -2500),
				new XY(2500, -2500),
				new XY(2500, 2500),
				new XY(-2500, 2500),
			})
			{
				IsClosed = true,
			});
			var giantInsert = new Insert(block)
			{
				InsertPoint = XYZ.Zero,
			};
			doc.Entities.Add(giantInsert);

			bool selected = PreviewExtentsSelector.TrySelect(
				doc.ModelSpace.Entities,
				Array.Empty<string>(),
				paddingModelUnits: 0.0,
				out PreviewExtentsSelection selection);

			Assert.True(selected);
			Assert.Contains(selection.ExcludedEntities, e => e.Handle == giantInsert.Handle.ToString("X") && e.Reason == "oversized-container");
			Assert.True(selection.Limits.Max.X < 500.0);
			Assert.True(selection.Limits.Min.X > -500.0);
			Assert.True(selection.Limits.Max.Y < 500.0);
			Assert.True(selection.Limits.Min.Y > -500.0);
		}

		[Fact]
		public void TrySelect_KeepsSingleLargeInsertWhenItIsTheOnlyGeometry()
		{
			CadDocument doc = new CadDocument();
			var block = new BlockRecord("ONLY-INSERT");
			block.Entities.Add(new LwPolyline(new[]
			{
				new XY(0, 0),
				new XY(1000, 0),
				new XY(1000, 500),
				new XY(0, 500),
			})
			{
				IsClosed = true,
			});
			var insert = new Insert(block)
			{
				InsertPoint = new XYZ(200, 300, 0),
			};
			doc.Entities.Add(insert);

			bool selected = PreviewExtentsSelector.TrySelect(
				doc.ModelSpace.Entities,
				Array.Empty<string>(),
				paddingModelUnits: 0.0,
				out PreviewExtentsSelection selection);

			Assert.True(selected);
			Assert.Equal(1, selection.CandidateCount);
			Assert.Equal(1, selection.IncludedCount);
			Assert.DoesNotContain(selection.ExcludedEntities, e => e.Handle == insert.Handle.ToString("X"));
			Assert.True(selection.Limits.Max.X > 1000.0);
			Assert.True(selection.Limits.Max.Y > 700.0);
		}

		[Fact]
		public void TrySelect_SemanticClusterKeepsNearbyDimensions()
		{
			CadDocument doc = new CadDocument();
			var cartogramLayer = new Layer("Картограмма");
			var dimensionLayer = new Layer("DIM");

			for (int i = 0; i < 60; i++)
			{
				double x = i * 10.0;
				doc.Entities.Add(new Line
				{
					StartPoint = new XYZ(x, 0.0, 0.0),
					EndPoint = new XYZ(x + 1.0, 0.0, 0.0),
					Layer = cartogramLayer,
				});
			}

			var nearDimensionA = new DimensionAligned(new XYZ(690.0, 0.0, 0.0), new XYZ(710.0, 0.0, 0.0))
			{
				DefinitionPoint = new XYZ(710.0, 20.0, 0.0),
				Layer = dimensionLayer,
			};
			var nearDimensionB = new DimensionAligned(new XYZ(692.0, 10.0, 0.0), new XYZ(712.0, 10.0, 0.0))
			{
				DefinitionPoint = new XYZ(712.0, 30.0, 0.0),
				Layer = dimensionLayer,
			};
			var farDimension = new DimensionAligned(new XYZ(1300.0, 0.0, 0.0), new XYZ(1320.0, 0.0, 0.0))
			{
				DefinitionPoint = new XYZ(1320.0, 20.0, 0.0),
				Layer = dimensionLayer,
			};

			doc.Entities.Add(nearDimensionA);
			doc.Entities.Add(nearDimensionB);
			doc.Entities.Add(farDimension);

			bool selected = PreviewExtentsSelector.TrySelect(
				doc.ModelSpace.Entities,
				Array.Empty<string>(),
				paddingModelUnits: 0.0,
				out PreviewExtentsSelection selection);

			Assert.True(selected);
			Assert.StartsWith("clustered-semantic-component-with-", selection.Strategy, StringComparison.Ordinal);
			Assert.Contains(nearDimensionA.Handle.ToString("X"), selection.IncludedHandles);
			Assert.Contains(nearDimensionB.Handle.ToString("X"), selection.IncludedHandles);
			Assert.DoesNotContain(farDimension.Handle.ToString("X"), selection.IncludedHandles);
			Assert.Contains(selection.ExcludedEntities, e => e.Handle == farDimension.Handle.ToString("X") && e.Reason == "outlier-cluster");
			Assert.True(selection.Limits.Max.X < 900.0);
		}

		[Fact]
		public void TrySelect_UsesDimensionBlockBoundsForPreviewLimits()
		{
			CadDocument doc = new CadDocument();
			var layer = new Layer("Картограмма");
			doc.Layers.Add(layer);

			var block = new BlockRecord("*D-preview");
			block.Entities.Add(new Line
			{
				StartPoint = new XYZ(0.0, 0.0, 0.0),
				EndPoint = new XYZ(160.0, 0.0, 0.0),
				Layer = layer,
			});
			doc.BlockRecords.Add(block);

			var dim = new DimensionLinear
			{
				FirstPoint = new XYZ(0.0, 0.0, 0.0),
				SecondPoint = new XYZ(10.0, 0.0, 0.0),
				DefinitionPoint = new XYZ(10.0, 5.0, 0.0),
				Block = block,
				Layer = layer,
			};
			doc.Entities.Add(dim);

			bool selected = PreviewExtentsSelector.TrySelect(
				doc.ModelSpace.Entities,
				Array.Empty<string>(),
				paddingModelUnits: 0.0,
				out PreviewExtentsSelection selection);

			Assert.True(selected);
			Assert.True(selection.Limits.Max.X >= 160.0);
		}

	}
}
