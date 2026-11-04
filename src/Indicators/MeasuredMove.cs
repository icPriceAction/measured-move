// =========================================================================================
// Tool Name:    Measured Move
// Class:        MeasuredMove
// Category:     Drawing Tool
// Version:      1.2.1
// Platform:     NinjaTrader 8.0+
// Author:       Istvan Cebrian
// Brand:        icPriceAction
// Website:      https://icpriceaction.com
// Repository:   https://github.com/icPriceAction/measured-move
// YouTube:      https://youtube.com/@icPriceAction
// Support:      https://buymeacoffee.com/icpriceaction
//
// License:      Free for personal trading use. Commercial resale, redistribution,
//               or repackaging is strictly prohibited. See LICENSE in official repository.
//
// Description:  Three-click Leg 1 = Leg 2 measured move projection. Projects leg 2 from the
//               pullback, marks the target price with a line and badge, and lets you drag
//               leg 2's duration without changing the target.
//
// Changelog:
//   v1.2.1 - Initial public release. Cleaned and optimized by icPriceAction.
//          - Direct2D brushes cached per render target and released on render target change and termination.
//          - Badge text layout cached; rebuilt only when target, leg size, time ratio, font or instrument changes.
//          - Pullback anchor seeded on the first click so no unplaced anchor is converted to screen coordinates.
//          - Moved to the icPA namespace; properties regrouped (1. General / 2. Visuals) with descriptions.
//   v1.2.0 - Time handle on the leg 2 arrowhead: drag to change leg 2 duration (Leg 2 Time Ratio); target price never changes.
//          - Projection measured in chart space: works on tick/volume/range/Renko charts and across session breaks and weekends.
//          - Leg 2 and the target previewed live while placing the pullback point; no phantom leg 2 while building.
//          - Magnet mode respected when editing anchors; whole-tool move uses MoveAnchor.
//   v1.1.0 - Prices formatted and rounded per instrument (FX, fractional bonds, etc).
//          - Badge sized to its text, kept inside the panel, placed below the line on bearish moves.
//          - Locked tools can no longer enter edit mode.
//   v1.0.0 - Tool stays visible when its anchors scroll off-screen (target line extends right).
//          - Standard NinjaTrader Stroke properties and font picker; antialiased rendering, cached text format, custom menu icon.
// =========================================================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using System.Windows.Input;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
#endregion

namespace NinjaTrader.NinjaScript.DrawingTools.icPA
{
	/// <summary>
	/// Measured Move (Leg 1 = Leg 2) projection tool.
	/// Click 1: leg 1 start. Click 2: leg 1 end. Click 3: pullback (leg 2 start).
	/// Drag the square handle on the arrowhead to change leg 2's duration without changing the target.
	/// </summary>
	public class MeasuredMove : DrawingTool
	{
		private enum DragMode
		{
			None,
			Leg1Start,
			Leg1End,
			Leg2Start,
			Leg2End,     // time handle (horizontal only)
			MovingTool
		}

		private struct MMGeometry
		{
			public Vector2 P1, P2, P3, P4;
			public double TargetPrice;
			public double LegPoints;
			public bool IsBullish;
		}

		private const float HandleRadius = 4f;
		private const float HandleHitRadius = 8f;
		private const float LineHitTolerance = 7f;
		private const double MinTimeRatio = 0.1;
		private const double MaxTimeRatio = 10.0;
		private const float BadgeBackgroundOpacity = 0.92f;

		private ChartAnchor anchorLeg1Start;
		private ChartAnchor anchorLeg1End;
		private ChartAnchor anchorLeg2Start;

		private DragMode currentDragMode = DragMode.None;

		// Last rendered badge rectangle, used for hit testing
		private RectangleF lastBadgeRect;
		private bool hasBadgeRect;

		// DirectWrite text resources (device independent, rebuilt only when inputs change)
		private SharpDX.DirectWrite.TextFormat cachedTextFormat;
		private NinjaTrader.Gui.Tools.SimpleFont cachedFont;
		private object cachedFontFamily;
		private double cachedFontSize;
		private bool cachedFontBold;
		private bool cachedFontItalic;

		private SharpDX.DirectWrite.TextLayout cachedBadgeLayout;
		private SharpDX.DirectWrite.TextFormat cachedBadgeFormat;
		private MasterInstrument cachedBadgeInstrument;
		private double cachedBadgeTarget = double.NaN;
		private double cachedBadgeLegPoints = double.NaN;
		private double cachedBadgeRatio = double.NaN;

		// Direct2D device resources (bound to one render target)
		private SharpDX.Direct2D1.RenderTarget dxTarget;
		private SharpDX.Direct2D1.SolidColorBrush dxHandleFill;
		private SharpDX.Direct2D1.SolidColorBrush dxHandleBorder;
		private SharpDX.Direct2D1.SolidColorBrush dxBadgeBorder;
		private SharpDX.Direct2D1.Brush dxBadgeBackground;
		private SharpDX.Direct2D1.Brush dxBadgeText;
		private System.Windows.Media.Brush dxBadgeBackgroundSource;
		private System.Windows.Media.Brush dxBadgeTextSource;

		public override object Icon
		{
			get
			{
				System.Windows.Media.Brush iconBrush = null;
				if (Application.Current != null)
					iconBrush = Application.Current.TryFindResource("FontActionBrush") as System.Windows.Media.Brush;
				if (iconBrush == null)
					iconBrush = System.Windows.Media.Brushes.Gray;

				var path = new System.Windows.Shapes.Path
				{
					Data = System.Windows.Media.Geometry.Parse("M1,15 L5,8 L8,11 L12,4 M9.5,4 L15,4"),
					Stroke = iconBrush,
					StrokeThickness = 1.5,
					StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
					StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
					StrokeLineJoin = System.Windows.Media.PenLineJoin.Round
				};
				var grid = new System.Windows.Controls.Grid { Width = 16, Height = 16 };
				grid.Children.Add(path);
				return grid;
			}
		}

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name = "Measured Move";
				Description =
					"Leg 1 = Leg 2 measured move projection. Click 1: leg 1 start, click 2: leg 1 end, click 3: pullback. "
					+ "The target is the pullback price plus leg 1's price distance, shown with a target line and badge. "
					+ "Drag the square arrowhead handle to change leg 2's duration without moving the target. Works on any chart and bar type.\r\n"
					+ "Developed by Istvan Cebrian | https://icpriceaction.com\r\n"
					+ "Support this free tool: https://buymeacoffee.com/icpriceaction";

				// All anchors start unplaced (IsEditing = true) and are placed one per click.
				anchorLeg1Start = new ChartAnchor { IsBrowsable = true, IsEditing = true, DrawingTool = this, DisplayName = "Leg 1 start" };
				anchorLeg1End   = new ChartAnchor { IsBrowsable = true, IsEditing = true, DrawingTool = this, DisplayName = "Leg 1 end" };
				anchorLeg2Start = new ChartAnchor { IsBrowsable = true, IsEditing = true, DrawingTool = this, DisplayName = "Leg 2 start (pullback)" };

				var connectorBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x71, 0x71, 0x71));
				connectorBrush.Freeze();

				LegStroke       = new Stroke(System.Windows.Media.Brushes.Green, DashStyleHelper.Solid, 2f);
				ConnectorStroke = new Stroke(connectorBrush, DashStyleHelper.Dot, 1.25f) { Opacity = 75 };
				TargetStroke    = new Stroke(System.Windows.Media.Brushes.Green, DashStyleHelper.Dash, 1.2f) { Opacity = 85 };

				BadgeTextColor = System.Windows.Media.Brushes.White;
				Font = new NinjaTrader.Gui.Tools.SimpleFont("Arial", 11) { Bold = true };

				Leg2TimeRatio = 1.0;
				ShowTargetBadge = true;
				ShowTargetHorizontal = true;
			}
			else if (State == State.Terminated)
			{
				// Terminated can run on instances that never rendered: every step is null-safe.
				DisposeDeviceResources();
				DisposeTextResources();
				Dispose();
			}
		}

		#region Anchors
		[Display(Order = 1)]
		public ChartAnchor AnchorLeg1Start { get { return anchorLeg1Start; } set { anchorLeg1Start = value; } }

		[Display(Order = 2)]
		public ChartAnchor AnchorLeg1End { get { return anchorLeg1End; } set { anchorLeg1End = value; } }

		[Display(Order = 3)]
		public ChartAnchor AnchorLeg2Start { get { return anchorLeg2Start; } set { anchorLeg2Start = value; } }

		public override IEnumerable<ChartAnchor> Anchors
		{
			get { return new[] { AnchorLeg1Start, AnchorLeg1End, AnchorLeg2Start }; }
		}

		public override bool SupportsAlerts { get { return false; } }
		#endregion

		#region Properties
		[Range(MinTimeRatio, MaxTimeRatio)]
		[Display(Name = "Leg 2 time ratio", Description = "Leg 2 duration relative to leg 1 (1 = same duration). Only changes the arrow's horizontal length, never the target price. Drag the square arrowhead handle to change it.", GroupName = "1. General", Order = 1)]
		public double Leg2TimeRatio { get; set; }

		[Display(Name = "Draw horizontal target line", Description = "Draw a horizontal line at the target price from the pullback to the right edge of the chart. When on, the badge is pinned to the right edge.", GroupName = "1. General", Order = 2)]
		public bool ShowTargetHorizontal { get; set; }

		[Display(Name = "Legs", Description = "Line style for leg 1 and the projected leg 2.", GroupName = "2. Visuals", Order = 1)]
		public Stroke LegStroke { get; set; }

		[Display(Name = "Retracement connector", Description = "Line style for the connector from the end of leg 1 to the pullback.", GroupName = "2. Visuals", Order = 2)]
		public Stroke ConnectorStroke { get; set; }

		[Display(Name = "Target line and badge", Description = "Line style for the horizontal target line. Its color is also used as the badge background.", GroupName = "2. Visuals", Order = 3)]
		public Stroke TargetStroke { get; set; }

		[Display(Name = "Show target badge", Description = "Show the label with the target price, leg size in points and ticks, and the time ratio when it is not 1.", GroupName = "2. Visuals", Order = 4)]
		public bool ShowTargetBadge { get; set; }

		[Display(Name = "Font", Description = "Font used for the target badge text.", GroupName = "2. Visuals", Order = 5)]
		public NinjaTrader.Gui.Tools.SimpleFont Font { get; set; }

		[XmlIgnore]
		[Display(Name = "Text color", Description = "Color of the target badge text.", GroupName = "2. Visuals", Order = 6)]
		public System.Windows.Media.Brush BadgeTextColor { get; set; }

		[Browsable(false)]
		public string BadgeTextColorSerializable
		{
			get { return Serialize.BrushToString(BadgeTextColor); }
			set { BadgeTextColor = Serialize.StringToBrush(value); }
		}
		#endregion

		#region Geometry
		private MMGeometry ComputeGeometry(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale)
		{
			var g = new MMGeometry();

			System.Windows.Point a = AnchorLeg1Start.GetPoint(chartControl, chartPanel, chartScale);
			System.Windows.Point b = AnchorLeg1End.GetPoint(chartControl, chartPanel, chartScale);
			System.Windows.Point c = AnchorLeg2Start.GetPoint(chartControl, chartPanel, chartScale);

			g.P1 = new Vector2((float)a.X, (float)a.Y);
			g.P2 = new Vector2((float)b.X, (float)b.Y);
			g.P3 = new Vector2((float)c.X, (float)c.Y);

			double delta = AnchorLeg1End.Price - AnchorLeg1Start.Price;
			double target = AnchorLeg2Start.Price + delta;

			MasterInstrument mi = chartControl.Instrument != null ? chartControl.Instrument.MasterInstrument : null;
			if (mi != null)
				target = mi.RoundToTickSize(target);

			g.TargetPrice = target;
			g.LegPoints = Math.Abs(delta);
			g.IsBullish = delta >= 0;

			// Horizontal projection in chart space: on equidistant charts this is a bar count,
			// so it is correct across session gaps and on non-time bar types, and it extends
			// naturally into the right margin.
			float x4 = g.P3.X + (g.P2.X - g.P1.X) * (float)Leg2TimeRatio;
			g.P4 = new Vector2(x4, (float)chartScale.GetYByValue(target));

			return g;
		}

		private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
		{
			Vector2 ab = b - a;
			float lenSq = ab.LengthSquared();
			if (lenSq < 0.0001f) return (p - a).Length();

			float t = Math.Max(0f, Math.Min(1f, Vector2.Dot(p - a, ab) / lenSq));
			return (p - (a + t * ab)).Length();
		}

		private static bool IsNear(Vector2 p, Vector2 q)
		{
			return (p - q).Length() <= HandleHitRadius;
		}

		private DragMode HitTest(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, System.Windows.Point point)
		{
			if (DrawingState == DrawingState.Building || AnchorLeg1Start == null || AnchorLeg1End == null || AnchorLeg2Start == null)
				return DragMode.None;

			MMGeometry g = ComputeGeometry(chartControl, chartPanel, chartScale);
			var p = new Vector2((float)point.X, (float)point.Y);

			// Handles first
			if (IsNear(p, g.P1)) return DragMode.Leg1Start;
			if (IsNear(p, g.P2)) return DragMode.Leg1End;
			if (IsNear(p, g.P3)) return DragMode.Leg2Start;
			if (IsNear(p, g.P4)) return DragMode.Leg2End;

			// Lines
			if (DistanceToSegment(p, g.P1, g.P2) <= LineHitTolerance ||
			    DistanceToSegment(p, g.P2, g.P3) <= LineHitTolerance ||
			    DistanceToSegment(p, g.P3, g.P4) <= LineHitTolerance)
				return DragMode.MovingTool;

			// Target line
			if (ShowTargetHorizontal)
			{
				float right = chartPanel.X + chartPanel.W;
				if (Math.Abs(p.Y - g.P4.Y) <= 5f && p.X >= Math.Min(g.P3.X, right) && p.X <= right)
					return DragMode.MovingTool;
			}

			// Badge
			if (ShowTargetBadge && hasBadgeRect &&
			    p.X >= lastBadgeRect.Left && p.X <= lastBadgeRect.Right &&
			    p.Y >= lastBadgeRect.Top && p.Y <= lastBadgeRect.Bottom)
				return DragMode.MovingTool;

			return DragMode.None;
		}

		private ChartAnchor AnchorFor(DragMode mode)
		{
			switch (mode)
			{
				case DragMode.Leg1Start: return AnchorLeg1Start;
				case DragMode.Leg1End:   return AnchorLeg1End;
				case DragMode.Leg2Start: return AnchorLeg2Start;
				default:                 return null;
			}
		}
		#endregion

		#region Interaction
		public override System.Windows.Point[] GetSelectionPoints(ChartControl chartControl, ChartScale chartScale)
		{
			if (DrawingState == DrawingState.Building)
				return new System.Windows.Point[0];

			ChartPanel chartPanel = chartControl.ChartPanels[chartScale.PanelIndex];
			MMGeometry g = ComputeGeometry(chartControl, chartPanel, chartScale);
			return new[]
			{
				new System.Windows.Point(g.P1.X, g.P1.Y),
				new System.Windows.Point(g.P2.X, g.P2.Y),
				new System.Windows.Point(g.P3.X, g.P3.Y),
				new System.Windows.Point(g.P4.X, g.P4.Y)
			};
		}

		public override bool IsVisibleOnChart(ChartControl chartControl, ChartScale chartScale, DateTime firstTimeOnChart, DateTime lastTimeOnChart)
		{
			if (DrawingState == DrawingState.Building)
				return true;

			// The target line and badge extend to the right edge, so the tool is visible
			// whenever any part of it starts at or before the last visible time.
			// Checked directly (no Anchors array / enumerator) because NT8 calls this often.
			DateTime earliest = DateTime.MaxValue;
			if (AnchorLeg1Start != null && AnchorLeg1Start.Time < earliest) earliest = AnchorLeg1Start.Time;
			if (AnchorLeg1End != null && AnchorLeg1End.Time < earliest)     earliest = AnchorLeg1End.Time;
			if (AnchorLeg2Start != null && AnchorLeg2Start.Time < earliest) earliest = AnchorLeg2Start.Time;

			return earliest <= lastTimeOnChart;
		}

		public override Cursor GetCursor(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, System.Windows.Point point)
		{
			switch (DrawingState)
			{
				case DrawingState.Building:
					return Cursors.Pen;

				case DrawingState.Moving:
					return IsLocked ? Cursors.No : Cursors.SizeAll;

				case DrawingState.Editing:
					if (IsLocked) return Cursors.No;
					return currentDragMode == DragMode.Leg2End ? Cursors.SizeWE : Cursors.Hand;

				default:
					DragMode hit = HitTest(chartControl, chartPanel, chartScale, point);
					switch (hit)
					{
						case DragMode.None:       return null;
						case DragMode.MovingTool: return IsLocked ? Cursors.Arrow : Cursors.SizeAll;
						case DragMode.Leg2End:    return IsLocked ? Cursors.Arrow : Cursors.SizeWE;
						default:                  return IsLocked ? Cursors.Arrow : Cursors.Hand;
					}
			}
		}

		public override void OnMouseDown(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, ChartAnchor dataPoint)
		{
			switch (DrawingState)
			{
				case DrawingState.Building:
					if (AnchorLeg1Start.IsEditing)
					{
						dataPoint.CopyDataValues(AnchorLeg1Start);
						dataPoint.CopyDataValues(AnchorLeg1End);   // leg 1 end follows the mouse from here
						dataPoint.CopyDataValues(AnchorLeg2Start); // seed only: keeps every anchor valid for coordinate conversion (not drawn until click 2)
						AnchorLeg1Start.IsEditing = false;
					}
					else if (AnchorLeg1End.IsEditing)
					{
						dataPoint.CopyDataValues(AnchorLeg1End);
						dataPoint.CopyDataValues(AnchorLeg2Start); // pullback point follows the mouse from here
						AnchorLeg1End.IsEditing = false;
					}
					else if (AnchorLeg2Start.IsEditing)
					{
						dataPoint.CopyDataValues(AnchorLeg2Start);
						AnchorLeg2Start.IsEditing = false;
						DrawingState = DrawingState.Normal;
						IsSelected = false;
					}
					break;

				case DrawingState.Normal:
					System.Windows.Point mousePt = dataPoint.GetPoint(chartControl, chartPanel, chartScale);
					DragMode hit = HitTest(chartControl, chartPanel, chartScale, mousePt);

					if (hit == DragMode.None)
					{
						IsSelected = false;
						currentDragMode = DragMode.None;
						break;
					}

					IsSelected = true;

					if (IsLocked)
					{
						currentDragMode = DragMode.None;
						break;
					}

					currentDragMode = hit;
					if (hit == DragMode.MovingTool)
					{
						DrawingState = DrawingState.Moving;
					}
					else
					{
						ChartAnchor anchor = AnchorFor(hit);
						if (anchor != null)
							anchor.IsEditing = true;
						DrawingState = DrawingState.Editing;
					}
					break;
			}
		}

		public override void OnMouseMove(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, ChartAnchor dataPoint)
		{
			if (IsLocked && DrawingState != DrawingState.Building)
				return;

			switch (DrawingState)
			{
				case DrawingState.Building:
					if (AnchorLeg1Start.IsEditing)
						break;
					if (AnchorLeg1End.IsEditing)
						dataPoint.CopyDataValues(AnchorLeg1End);
					else if (AnchorLeg2Start.IsEditing)
						dataPoint.CopyDataValues(AnchorLeg2Start);
					break;

				case DrawingState.Editing:
					if (currentDragMode == DragMode.Leg2End)
					{
						MMGeometry g = ComputeGeometry(chartControl, chartPanel, chartScale);
						float leg1Width = g.P2.X - g.P1.X;
						if (Math.Abs(leg1Width) >= 1f)
						{
							System.Windows.Point mousePt = dataPoint.GetPoint(chartControl, chartPanel, chartScale);
							double ratio = (mousePt.X - g.P3.X) / leg1Width;
							ratio = Math.Max(MinTimeRatio, Math.Min(MaxTimeRatio, ratio));
							Leg2TimeRatio = Math.Round(ratio, 2);
						}
					}
					else
					{
						// CopyDataValues respects magnet mode (snap to OHLC)
						ChartAnchor anchor = AnchorFor(currentDragMode);
						if (anchor != null)
							dataPoint.CopyDataValues(anchor);
					}
					break;

				case DrawingState.Moving:
					foreach (ChartAnchor anchor in Anchors)
						anchor.MoveAnchor(InitialMouseDownAnchor, dataPoint, chartControl, chartPanel, chartScale, this);
					break;
			}
		}

		public override void OnMouseUp(ChartControl chartControl, ChartPanel chartPanel, ChartScale chartScale, ChartAnchor dataPoint)
		{
			if (DrawingState == DrawingState.Building)
				return;

			foreach (ChartAnchor anchor in Anchors)
				anchor.IsEditing = false;

			currentDragMode = DragMode.None;
			DrawingState = DrawingState.Normal;
		}
		#endregion

		#region Device and text resources
		public override void OnRenderTargetChanged()
		{
			DisposeDeviceResources();
			if (RenderTarget != null)
				CreateDeviceResources(RenderTarget);
		}

		private void CreateDeviceResources(SharpDX.Direct2D1.RenderTarget rt)
		{
			dxHandleFill   = new SharpDX.Direct2D1.SolidColorBrush(rt, new Color4(1f, 1f, 1f, 1f));
			dxHandleBorder = new SharpDX.Direct2D1.SolidColorBrush(rt, new Color4(0.2f, 0.2f, 0.2f, 1f));
			dxBadgeBorder  = new SharpDX.Direct2D1.SolidColorBrush(rt, new Color4(0f, 0f, 0f, 0.35f));
			dxTarget = rt;
		}

		/// <summary>
		/// Cheap reference checks only. Brushes are rebuilt when the render target changes
		/// (safety net alongside OnRenderTargetChanged) or when the user picks a new color,
		/// never on an ordinary frame.
		/// </summary>
		private void EnsureDeviceResources(SharpDX.Direct2D1.RenderTarget rt)
		{
			if (!ReferenceEquals(rt, dxTarget))
			{
				DisposeDeviceResources();
				CreateDeviceResources(rt);
			}

			System.Windows.Media.Brush bgSource = TargetStroke != null ? TargetStroke.Brush : null;
			if (!ReferenceEquals(bgSource, dxBadgeBackgroundSource) || (dxBadgeBackground == null && bgSource != null))
			{
				if (dxBadgeBackground != null)
				{
					dxBadgeBackground.Dispose();
					dxBadgeBackground = null;
				}
				if (bgSource != null)
				{
					dxBadgeBackground = bgSource.ToDxBrush(rt);
					dxBadgeBackground.Opacity = BadgeBackgroundOpacity;
				}
				dxBadgeBackgroundSource = bgSource;
			}

			System.Windows.Media.Brush textSource = BadgeTextColor;
			if (!ReferenceEquals(textSource, dxBadgeTextSource) || (dxBadgeText == null && textSource != null))
			{
				if (dxBadgeText != null)
				{
					dxBadgeText.Dispose();
					dxBadgeText = null;
				}
				if (textSource != null)
					dxBadgeText = textSource.ToDxBrush(rt);
				dxBadgeTextSource = textSource;
			}
		}

		private void DisposeDeviceResources()
		{
			if (dxHandleFill != null)      { dxHandleFill.Dispose();      dxHandleFill = null; }
			if (dxHandleBorder != null)    { dxHandleBorder.Dispose();    dxHandleBorder = null; }
			if (dxBadgeBorder != null)     { dxBadgeBorder.Dispose();     dxBadgeBorder = null; }
			if (dxBadgeBackground != null) { dxBadgeBackground.Dispose(); dxBadgeBackground = null; }
			if (dxBadgeText != null)       { dxBadgeText.Dispose();       dxBadgeText = null; }

			dxBadgeBackgroundSource = null;
			dxBadgeTextSource = null;
			dxTarget = null;
		}

		private void DisposeTextResources()
		{
			if (cachedBadgeLayout != null)
			{
				cachedBadgeLayout.Dispose();
				cachedBadgeLayout = null;
			}
			if (cachedTextFormat != null)
			{
				cachedTextFormat.Dispose();
				cachedTextFormat = null;
			}

			cachedBadgeFormat = null;
			cachedBadgeInstrument = null;
			cachedFont = null;
			cachedFontFamily = null;
		}

		private SharpDX.DirectWrite.TextFormat GetTextFormat()
		{
			if (Font == null)
				return null;

			// Field-by-field comparison: no string allocation per frame.
			if (cachedTextFormat == null
			    || !ReferenceEquals(Font, cachedFont)
			    || !object.Equals(Font.Family, cachedFontFamily)
			    || Font.Size != cachedFontSize
			    || Font.Bold != cachedFontBold
			    || Font.Italic != cachedFontItalic)
			{
				if (cachedTextFormat != null)
				{
					cachedTextFormat.Dispose();
					cachedTextFormat = null;
				}

				cachedTextFormat = Font.ToDirectWriteTextFormat();
				cachedTextFormat.WordWrapping = SharpDX.DirectWrite.WordWrapping.NoWrap;

				cachedFont = Font;
				cachedFontFamily = Font.Family;
				cachedFontSize = Font.Size;
				cachedFontBold = Font.Bold;
				cachedFontItalic = Font.Italic;
			}
			return cachedTextFormat;
		}

		/// <summary>
		/// Returns the badge text layout, rebuilding it (and its text) only when the target,
		/// leg size, time ratio, font or instrument changes.
		/// </summary>
		private SharpDX.DirectWrite.TextLayout GetBadgeLayout(ChartControl chartControl, MMGeometry g)
		{
			SharpDX.DirectWrite.TextFormat textFormat = GetTextFormat();
			if (textFormat == null)
				return null;

			MasterInstrument mi = chartControl.Instrument != null ? chartControl.Instrument.MasterInstrument : null;

			if (cachedBadgeLayout != null
			    && ReferenceEquals(textFormat, cachedBadgeFormat)
			    && ReferenceEquals(mi, cachedBadgeInstrument)
			    && g.TargetPrice == cachedBadgeTarget
			    && g.LegPoints == cachedBadgeLegPoints
			    && Leg2TimeRatio == cachedBadgeRatio)
				return cachedBadgeLayout;

			double tickSize = mi != null ? mi.TickSize : 0;

			string targetText = mi != null ? mi.FormatPrice(g.TargetPrice) : g.TargetPrice.ToString("F2");
			string pointsText = mi != null ? mi.FormatPrice(g.LegPoints) : g.LegPoints.ToString("F2");
			double ticks = tickSize > 0 ? Math.Round(g.LegPoints / tickSize) : 0;

			string text = string.Format("MM Target: {0}  |  {1} pts ({2:F0} t)", targetText, pointsText, ticks);
			if (Math.Abs(Leg2TimeRatio - 1.0) > 0.005)
				text += string.Format("  |  t\u00D7{0:0.##}", Leg2TimeRatio);

			if (cachedBadgeLayout != null)
			{
				cachedBadgeLayout.Dispose();
				cachedBadgeLayout = null;
			}

			cachedBadgeLayout = new SharpDX.DirectWrite.TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory, text, textFormat, 2000f, 200f);
			cachedBadgeFormat = textFormat;
			cachedBadgeInstrument = mi;
			cachedBadgeTarget = g.TargetPrice;
			cachedBadgeLegPoints = g.LegPoints;
			cachedBadgeRatio = Leg2TimeRatio;

			return cachedBadgeLayout;
		}
		#endregion

		#region Rendering
		public override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			hasBadgeRect = false;

			if (AnchorLeg1Start == null || AnchorLeg1End == null || AnchorLeg2Start == null)
				return;
			if (LegStroke == null || ConnectorStroke == null || TargetStroke == null)
				return;

			bool building = DrawingState == DrawingState.Building;
			if (building && AnchorLeg1Start.IsEditing)
				return; // nothing placed yet

			SharpDX.Direct2D1.RenderTarget rt = RenderTarget;
			if (rt == null)
				return;

			EnsureDeviceResources(rt);

			ChartPanel chartPanel = chartControl.ChartPanels[chartScale.PanelIndex];
			bool leg2Ready = !(building && AnchorLeg1End.IsEditing);

			MMGeometry g = ComputeGeometry(chartControl, chartPanel, chartScale);

			SharpDX.Direct2D1.AntialiasMode oldMode = rt.AntialiasMode;
			rt.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;

			try
			{
				// 1. Leg 1
				LegStroke.RenderTarget = rt;
				rt.DrawLine(g.P1, g.P2, LegStroke.BrushDX, LegStroke.Width, LegStroke.StrokeStyle);
				DrawArrowCap(rt, g.P1, g.P2, LegStroke);

				if (leg2Ready)
				{
					// 2. Retracement connector
					ConnectorStroke.RenderTarget = rt;
					rt.DrawLine(g.P2, g.P3, ConnectorStroke.BrushDX, ConnectorStroke.Width, ConnectorStroke.StrokeStyle);

					// 3. Leg 2 projection (previewed live while placing the pullback)
					rt.DrawLine(g.P3, g.P4, LegStroke.BrushDX, LegStroke.Width, LegStroke.StrokeStyle);
					DrawArrowCap(rt, g.P3, g.P4, LegStroke);

					// 4. Horizontal target line
					float right = chartPanel.X + chartPanel.W;
					if (ShowTargetHorizontal)
					{
						TargetStroke.RenderTarget = rt;
						rt.DrawLine(new Vector2(g.P3.X, g.P4.Y), new Vector2(right, g.P4.Y),
							TargetStroke.BrushDX, TargetStroke.Width, TargetStroke.StrokeStyle);
					}

					// 5. Badge
					if (ShowTargetBadge)
						DrawBadge(rt, chartControl, chartPanel, g);
				}

				// 6. Handles
				if (IsSelected && !building && dxHandleFill != null && dxHandleBorder != null)
				{
					DrawCircleHandle(rt, g.P1, dxHandleFill, dxHandleBorder);
					DrawCircleHandle(rt, g.P2, dxHandleFill, dxHandleBorder);
					DrawCircleHandle(rt, g.P3, dxHandleFill, dxHandleBorder);
					DrawSquareHandle(rt, g.P4, dxHandleFill, dxHandleBorder); // time handle: different shape
				}
			}
			finally
			{
				rt.AntialiasMode = oldMode;
			}
		}

		private void DrawBadge(SharpDX.Direct2D1.RenderTarget rt, ChartControl chartControl, ChartPanel chartPanel, MMGeometry g)
		{
			if (dxBadgeBackground == null || dxBadgeText == null || dxBadgeBorder == null)
				return;

			SharpDX.DirectWrite.TextLayout layout = GetBadgeLayout(chartControl, g);
			if (layout == null)
				return;

			const float padX = 7f;
			const float padY = 3f;
			SharpDX.DirectWrite.TextMetrics metrics = layout.Metrics;
			float w = metrics.Width + 2f * padX;
			float h = metrics.Height + 2f * padY;

			float left = chartPanel.X;
			float right = chartPanel.X + chartPanel.W;

			// Pinned to the right edge when the target line is shown, otherwise next to the arrowhead.
			float bx = ShowTargetHorizontal ? right - w - 8f : g.P4.X + 8f;
			bx = Math.Max(left + 2f, Math.Min(bx, right - w - 2f));

			// Above the line for bullish moves, below for bearish.
			float by = g.IsBullish ? g.P4.Y - h - 4f : g.P4.Y + 4f;

			var rect = new RectangleF(bx, by, w, h);
			var pill = new RoundedRectangle { Rect = rect, RadiusX = 4f, RadiusY = 4f };

			lastBadgeRect = rect;
			hasBadgeRect = true;

			rt.FillRoundedRectangle(pill, dxBadgeBackground);
			rt.DrawRoundedRectangle(pill, dxBadgeBorder, 1f);
			rt.DrawTextLayout(new Vector2(bx + padX, by + padY), layout, dxBadgeText);
		}

		private static void DrawArrowCap(SharpDX.Direct2D1.RenderTarget rt, Vector2 start, Vector2 end, Stroke stroke)
		{
			Vector2 dir = end - start;
			float len = dir.Length();
			if (len < 5f) return;
			dir /= len;

			var normal = new Vector2(-dir.Y, dir.X);
			float size = 6f + stroke.Width * 1.5f;
			Vector2 pLeft = end - dir * size + normal * (size * 0.5f);
			Vector2 pRight = end - dir * size - normal * (size * 0.5f);

			rt.DrawLine(end, pLeft, stroke.BrushDX, stroke.Width);
			rt.DrawLine(end, pRight, stroke.BrushDX, stroke.Width);
		}

		private static void DrawCircleHandle(SharpDX.Direct2D1.RenderTarget rt, Vector2 center,
			SharpDX.Direct2D1.Brush fill, SharpDX.Direct2D1.Brush border)
		{
			var ellipse = new SharpDX.Direct2D1.Ellipse(center, HandleRadius, HandleRadius);
			rt.FillEllipse(ellipse, fill);
			rt.DrawEllipse(ellipse, border, 1.5f);
		}

		private static void DrawSquareHandle(SharpDX.Direct2D1.RenderTarget rt, Vector2 center,
			SharpDX.Direct2D1.Brush fill, SharpDX.Direct2D1.Brush border)
		{
			var rect = new RectangleF(center.X - HandleRadius, center.Y - HandleRadius, HandleRadius * 2f, HandleRadius * 2f);
			rt.FillRectangle(rect, fill);
			rt.DrawRectangle(rect, border, 1.5f);
		}
		#endregion
	}
}