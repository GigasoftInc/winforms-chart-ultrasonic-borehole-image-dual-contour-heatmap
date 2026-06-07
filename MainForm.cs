using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Gigasoft.ProEssentials;
using Gigasoft.ProEssentials.Enums;

namespace WirelineUbi
{
    /// <summary>
    /// ProEssentials WinForms Wireline UBI — Ultrasonic Borehole Imager dual contour  (.NET 8)
    ///
    /// Code-built WinForms port. Two co-registered azimuthal channels (AWCN
    /// amplitude + TTCN transit time) are concatenated along X with a null-data
    /// gutter and rendered as a single dual heatmap in one Pesgo chart. A
    /// top-right overlay button toggles null-rendering (Black / Gaps).
    ///
    /// PORT NOTES (only WPF host pieces changed; the parallel loader, the
    /// channel concatenation, the PeCustomGridNumber handler, and the entire
    /// ProEssentials chart configuration are IDENTICAL to the WPF code-behind):
    ///   - SystemParameters.WorkArea  -> Screen.PrimaryScreen.WorkingArea
    ///   - MessageBox / MessageBoxButton.OK -> WinForms MessageBox / MessageBoxButtons.OK
    ///   - Application.Current.Shutdown()    -> Application.Exit()
    ///   - System.Windows.Media.Color        -> System.Drawing.Color
    ///   - WPF overlay Button in same Grid cell -> WinForms Button parented to the
    ///       chart control (Pesgo1.Controls.Add) and anchored top-right so it
    ///       floats over the chart.
    /// </summary>
    public class MainForm : Form
    {
        private Pesgo Pesgo1;
        private Button NullModeToggle;

        // ===== Data files =====
        private const string AWCN_FILE = "TestData_FORGE_AWCN_clipped.txt";
        private const string TTCN_FILE = "TestData_FORGE_TTCN_clipped.txt";

        // ===== Layout constants =====
        private const int N_AZ_PER_CHANNEL = 180;
        private const int N_GAP_COLS = 9;                 // 5% gutter

        private const float TTCN_X_OFFSET = 378.0f;
        private const float X_AXIS_MIN = 0.0f;
        private const float X_AXIS_MAX = 738.0f;

        private const float NULL_VALUE_IN_Z_GRID = 0.0f;

        private enum NullRenderMode { Black, Gaps }
        private NullRenderMode _nullRendering = NullRenderMode.Black;

        public MainForm()
        {
            Pesgo1 = new Pesgo();
            Pesgo1.Dock = DockStyle.Fill;
            Controls.Add(Pesgo1);

            // Overlay toggle button: WPF placed it in the same Grid cell with
            // Top/Right alignment so it floats over the chart. In WinForms we
            // parent the button to the chart control itself and anchor it
            // top-right, which reproduces the floating overlay.
            NullModeToggle = new Button
            {
                Text = "Show Nulls as Holes",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlatStyle = FlatStyle.Standard,
                Font = new Font("Segoe UI", 8.25f),
                Padding = new Padding(10, 4, 10, 4),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            var tip = new ToolTip();
            tip.SetToolTip(NullModeToggle,
                "Toggle PE's null-rendering mode. Black: nulls become Z=0, render as black. " +
                "Gaps: nulls preserved, PE leaves contour holes (white-speckle artifact).");
            NullModeToggle.Click += NullModeToggle_Click;
            Pesgo1.Controls.Add(NullModeToggle);
            // Position top-right with an 8/18 margin once the chart has a size.
            PositionToggle();
            Pesgo1.SizeChanged += (s, e) => PositionToggle();

            Text = "ProEssentials — Wireline UBI Dual-Contour Borehole Image";
            ClientSize = new Size(1100, 900);
            MinimumSize = new Size(700, 600);
            StartPosition = FormStartPosition.Manual;

            // WPF used SystemParameters.WorkArea; WinForms uses Screen.WorkingArea.
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            Height = work.Height;
            Top    = work.Top;
            Left   = work.Left + (work.Width - Width) / 2;

            this.Load += MainForm_Load;
            this.FormClosing += MainForm_FormClosing;
        }

        private void PositionToggle()
        {
            NullModeToggle.Location = new Point(
                Pesgo1.ClientSize.Width - NullModeToggle.Width - 18,
                8);
        }

        // -----------------------------------------------------------------------
        // MainForm_Load — single full chart build at startup (parallel load)
        // -----------------------------------------------------------------------
        private void MainForm_Load(object sender, EventArgs e)
        {
            float[] depthsAwcn, depthsTtcn;
            float[] azAwcn, azTtcn;
            float[,] zAwcn, zTtcn;

            try
            {
                var awcnTask = Task.Run(() => LoadAzimuthalTestDataFast(AWCN_FILE));
                var ttcnTask = Task.Run(() => LoadAzimuthalTestDataFast(TTCN_FILE));
                Task.WaitAll(awcnTask, ttcnTask);

                (depthsAwcn, azAwcn, zAwcn) = awcnTask.Result;
                (depthsTtcn, azTtcn, zTtcn) = ttcnTask.Result;
            }
            catch (AggregateException agg)
            {
                var inner = agg.InnerException ?? agg;
                MessageBox.Show(
                    $"Failed to load UBI data\n\n{inner.Message}\n\n" +
                    $"Make sure {AWCN_FILE} and {TTCN_FILE} are in the same " +
                    "folder as the executable.",
                    "UBI data load error", MessageBoxButtons.OK);
                Application.Exit();
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to load UBI data\n\n{ex.Message}",
                    "UBI data load error", MessageBoxButtons.OK);
                Application.Exit();
                return;
            }

            if (depthsAwcn.Length != depthsTtcn.Length)
            {
                MessageBox.Show(
                    $"AWCN and TTCN have different depth counts " +
                    $"({depthsAwcn.Length} vs {depthsTtcn.Length}). " +
                    "These channels must be co-registered.",
                    "UBI data mismatch", MessageBoxButtons.OK);
                Application.Exit();
                return;
            }

            ConcatenateChannels(
                depthsAwcn, azAwcn, zAwcn, azTtcn, zTtcn,
                out float[] depths, out float[] xAxis, out float[,] zCombined);

            ConfigureAndPushChart(depths, xAxis, zCombined);
            UpdateToggleButtonLabel();
        }

        // -----------------------------------------------------------------------
        // NullModeToggle_Click — minimal toggle, no data re-push
        // -----------------------------------------------------------------------
        private void NullModeToggle_Click(object sender, EventArgs e)
        {
            _nullRendering = (_nullRendering == NullRenderMode.Black)
                ? NullRenderMode.Gaps
                : NullRenderMode.Black;

            Pesgo1.PeData.NullDataValueZ =
                _nullRendering == NullRenderMode.Black ? -999.0f : 0.0f;

            Pesgo1.PeFunction.Force3dxVerticeRebuild = true;
            Pesgo1.PeFunction.ReinitializeResetImage();
            Pesgo1.Invalidate();

            UpdateToggleButtonLabel();
        }

        private void UpdateToggleButtonLabel()
        {
            NullModeToggle.Text = _nullRendering == NullRenderMode.Black
                ? "Show Nulls as Gaps"
                : "Show Nulls as Black";
            PositionToggle();
        }

        // -----------------------------------------------------------------------
        // ConfigureAndPushChart — full chart configuration at startup
        // BELOW: IDENTICAL to WPF except Media.Color → Drawing.Color.
        // CALL ORDER MATTERS — see the per-step notes.
        // -----------------------------------------------------------------------
        private void ConfigureAndPushChart(
            float[] depths, float[] xAxis, float[,] zCombined)
        {
            int nDepths = depths.Length;
            int nCols = xAxis.Length;

            // Step 1 — data dimensions
            Pesgo1.PeData.Subsets = nDepths;
            Pesgo1.PeData.Points = nCols;

            Pesgo1.PeData.DuplicateDataX = DuplicateData.PointIncrement;
            Pesgo1.PeData.DuplicateDataY = DuplicateData.SubsetIncrement;

            // Step 2 — pre-allocate arrays
            Pesgo1.PeData.X[0, nCols - 1] = 0;
            Pesgo1.PeData.Y[0, nDepths - 1] = 0;
            Pesgo1.PeData.Z[nDepths - 1, nCols - 1] = 0;

            // Step 3 — bulk data push via FastCopyFrom
            Pesgo1.PeData.X.FastCopyFrom(xAxis, nCols);

            float[] yNegated = new float[nDepths];
            for (int s = 0; s < nDepths; s++)
                yNegated[s] = -depths[s];
            Pesgo1.PeData.Y.FastCopyFrom(yNegated, nDepths);

            Pesgo1.PeData.Z.FastCopyFrom(zCombined);

            // Step 4 — Z-range pin (must be set AFTER Z data exists)
            Pesgo1.PeGrid.Configure.ManualScaleControlZ = ManualScaleControl.MinMax;
            Pesgo1.PeGrid.Configure.ManualMinZ = 0.0F;
            Pesgo1.PeGrid.Configure.ManualMaxZ = 15.0F;

            Pesgo1.PeLegend.ContourLegendII.ManualContourScaleControl = ManualScaleControl.MinMax;
            Pesgo1.PeLegend.ContourLegendII.ManualContourMin = 0.0;
            Pesgo1.PeLegend.ContourLegendII.ManualContourMax = 15.0;

            // Step 5 — null sentinel (initial mode is Black)
            Pesgo1.PeData.NullDataValueZ =
                _nullRendering == NullRenderMode.Black ? -999.0f : 0.0f;

            Pesgo1.PeData.Filter2D = Filter2D.Disable;

            Pesgo1.PeGrid.Option.InvertedYAxis = true;

            // Step 6 — axis labels and titles
            Pesgo1.PeString.XAxisLabel = "Azimuth — North orientation, AWCN (left) | TTCN (right)";
            Pesgo1.PeString.YAxisLabel = "Depth ( ft )";

            Pesgo1.PeConfigure.ImageAdjustLeft = 50;
            Pesgo1.PeConfigure.ImageAdjustBottom = 50;

            Pesgo1.PeGrid.Configure.ManualScaleControlX = ManualScaleControl.MinMax;
            Pesgo1.PeGrid.Configure.ManualMinX = X_AXIS_MIN;
            Pesgo1.PeGrid.Configure.ManualMaxX = X_AXIS_MAX;

            Pesgo1.PeGrid.Configure.ManualXAxisTicknLine = true;
            Pesgo1.PeGrid.Configure.ManualXAxisLine = 90;
            Pesgo1.PeGrid.Configure.ManualXAxisTick = 30;
            Pesgo1.PeGrid.Option.CustomGridNumbersX = true;

            Pesgo1.PeColor.GridBold = true;

            // Step 7 — visual styling — light theme matches field-print
            Pesgo1.PeColor.BitmapGradientMode = true;
            Pesgo1.PeColor.QuickStyle = QuickStyle.LightNoBorder;
            Pesgo1.PeColor.GraphGradientStyle = GradientStyle.Horizontal;
            Pesgo1.PeColor.GraphGradientStart = Color.FromArgb(255, 250, 246, 235);
            Pesgo1.PeColor.GraphGradientEnd   = Color.FromArgb(255, 250, 246, 235);

            // Custom afmhot-style 8-stop hot color ramp
            Pesgo1.PePlot.Allow.ContourColors = true;
            Pesgo1.PePlot.Allow.ContourColorsShadows = true;

            Pesgo1.PeColor.ContourColorBlends = 10;

            Pesgo1.PeColor.ContourColors.Clear();
            Pesgo1.PeColor.ContourColors[0] = Color.FromArgb(  0,   0,   0); // black
            Pesgo1.PeColor.ContourColors[1] = Color.FromArgb( 70,   0,   0); // very dark red
            Pesgo1.PeColor.ContourColors[2] = Color.FromArgb(160,  30,   0); // dark red
            Pesgo1.PeColor.ContourColors[3] = Color.FromArgb(220,  70,  10); // red-orange
            Pesgo1.PeColor.ContourColors[4] = Color.FromArgb(255, 130,  30); // orange
            Pesgo1.PeColor.ContourColors[5] = Color.FromArgb(255, 190,  70); // amber
            Pesgo1.PeColor.ContourColors[6] = Color.FromArgb(255, 230, 140); // pale yellow
            Pesgo1.PeColor.ContourColors[7] = Color.FromArgb(255, 255, 255); // white

            Pesgo1.PeColor.ContourColorSet = ContourColorSet.ContourColors;

            Pesgo1.PeLegend.ContourLegendPrecision = ContourLegendPrecision.ZeroDecimals;
            Pesgo1.PeLegend.ContourStyle = true;
            Pesgo1.PeLegend.Location = LegendLocation.Top;
            Pesgo1.PeString.ContourLabels[0]  = "0";
            Pesgo1.PeString.ContourLabels[35] = "7.5";
            Pesgo1.PeString.ContourLabels[70] = "15";

            Pesgo1.PePlot.Method = SGraphPlottingMethod.ContourColors;

            Pesgo1.PeUserInterface.Menu.DataShadow = MenuControl.Hide;

            // Step 8 — zoom and interaction
            Pesgo1.PeUserInterface.Scrollbar.MouseWheelZoomFactor = 1.4F;
            Pesgo1.PeUserInterface.Scrollbar.MouseWheelZoomSmoothness = 2;
            Pesgo1.PeGrid.GridBands = false;

            Pesgo1.PeUserInterface.Allow.ZoomStyle = ZoomStyle.Ro2Not;
            Pesgo1.PeUserInterface.Allow.Zooming = AllowZooming.HorzAndVert;
            Pesgo1.PeUserInterface.Scrollbar.MouseWheelFunction = MouseWheelFunction.HorizontalVerticalZoom;

            Pesgo1.PeUserInterface.Scrollbar.ScrollingVertZoom = true;
            Pesgo1.PeUserInterface.Scrollbar.ScrollingHorzZoom = true;

            // Step 9 — compass markers via X-axis line annotations
            Pesgo1.PeAnnotation.Show = true;
            Pesgo1.PeAnnotation.ShowAnnotationText = true;
            Pesgo1.PeAnnotation.InFront = true;

            double[] compassX     = { 0, 90, 180, 270, 360, 378, 468, 558, 648, 738 };
            string[] compassLabel = { "N", "E", "S", "W", "N", "N", "E", "S", "W", "N" };

            for (int i = 0; i < compassX.Length; i++)
            {
                Pesgo1.PeAnnotation.Line.XAxis[i] = compassX[i];
                Pesgo1.PeAnnotation.Line.XAxisType[i] = LineAnnotationType.Dot;
                Pesgo1.PeAnnotation.Line.XAxisColor[i] = Color.FromArgb(180, 60, 60, 60);
                Pesgo1.PeAnnotation.Line.XAxisText[i] = compassLabel[i];
                Pesgo1.PeAnnotation.Line.XAxisInFront[i] = AnnotationInFront.InFront;
            }

            // Step 10 — legend, grid, plot-method allow
            Pesgo1.PeGrid.InFront = true;
            Pesgo1.PeGrid.LineControl = GridLineControl.YAxis;
            Pesgo1.PeGrid.Style = GridStyle.Dot;

            Pesgo1.PePlot.Allow.Line = false;
            Pesgo1.PePlot.Allow.Point = false;
            Pesgo1.PePlot.Allow.Bar = false;
            Pesgo1.PePlot.Allow.Area = false;
            Pesgo1.PePlot.Allow.Spline = false;
            Pesgo1.PePlot.Allow.SplineArea = false;
            Pesgo1.PePlot.Allow.PointsPlusLine = false;
            Pesgo1.PePlot.Allow.PointsPlusSpline = false;
            Pesgo1.PePlot.Allow.BestFitCurve = false;
            Pesgo1.PePlot.Allow.BestFitLine = false;
            Pesgo1.PePlot.Allow.Stick = false;

            // Step 11 — titles and fonts
            Pesgo1.PeString.MainTitle =
                "Wireline UBI — FORGE 16A(78)-32 R3B HF — AWCN + TTCN Dual Contour";
            Pesgo1.PeString.SubTitle =
                $"AWCN amplitude (left) | TTCN transit time (right) — 0–15 normalized — " +
                $"{nDepths} depths × {nCols} cols (180 + {N_GAP_COLS} gap + 180)";

            Pesgo1.PeGrid.Configure.AutoMinMaxPadding = 0;

            Pesgo1.PeFont.FontSize = Gigasoft.ProEssentials.Enums.FontSize.Medium;
            Pesgo1.PeFont.Fixed = true;

            Pesgo1.PeUserInterface.Dialog.Axis = false;
            Pesgo1.PeUserInterface.Dialog.Style = false;
            Pesgo1.PeUserInterface.Dialog.Subsets = false;

            Pesgo1.PeConfigure.TextShadows = TextShadows.BoldText;
            Pesgo1.PeFont.MainTitle.Bold = true;
            Pesgo1.PeFont.SubTitle.Bold = true;
            Pesgo1.PeFont.Label.Bold = true;

            // Step 12 — export defaults
            Pesgo1.PeSpecial.DpiX = 600;
            Pesgo1.PeSpecial.DpiY = 600;
            Pesgo1.PeUserInterface.Dialog.AllowEmfExport = false;
            Pesgo1.PeUserInterface.Dialog.AllowWmfExport = false;
            Pesgo1.PeUserInterface.Dialog.ExportSizeDef = ExportSizeDef.NoSizeOrPixel;
            Pesgo1.PeUserInterface.Dialog.ExportTypeDef = ExportTypeDef.Png;
            Pesgo1.PeUserInterface.Dialog.ExportDestDef = ExportDestDef.Clipboard;
            Pesgo1.PeUserInterface.Dialog.ExportUnitXDef = "1280";
            Pesgo1.PeUserInterface.Dialog.ExportUnitYDef = "768";
            Pesgo1.PeUserInterface.Dialog.ExportImageDpi = 300;

            // Step 13 — GPU rendering pipeline
            Pesgo1.PeConfigure.Composite2D3D = Composite2D3D.Foreground;
            Pesgo1.PeConfigure.RenderEngine = RenderEngine.Direct3D;
            Pesgo1.PeData.ComputeShader = true;

            // Step 14 — XYZ cursor prompt
            Pesgo1.PeUserInterface.Cursor.PromptLocation = CursorPromptLocation.ToolTip;
            Pesgo1.PeUserInterface.Cursor.PromptTracking = true;
            Pesgo1.PeUserInterface.Cursor.PromptStyle = CursorPromptStyle.XYValues;
            Pesgo1.PeUserInterface.Cursor.HourGlassThreshold = 9999999;

            Pesgo1.PeCustomGridNumber += Pesgo1_PeCustomGridNumber;

            Pesgo1.PeFunction.Force3dxNewColors = true;
            Pesgo1.PeFunction.Force3dxVerticeRebuild = true;

            // Step 15 — render
            Pesgo1.PeFunction.ReinitializeResetImage();
            Pesgo1.Invalidate();
        }

        // =======================================================================
        // LoadAzimuthalTestDataFast — single-pass span-based loader
        // IDENTICAL to WPF. Pure C# / System.IO — framework-agnostic.
        // =======================================================================
        private static (float[] depths, float[] azimuths, float[,] values)
            LoadAzimuthalTestDataFast(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);

            int byteStart = 0;
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                byteStart = 2;

            string content = Encoding.Unicode.GetString(
                bytes, byteStart, bytes.Length - byteStart);

            ReadOnlySpan<char> span = content.AsSpan();

            int newlineCount = 0;
            for (int i = 0; i < span.Length; i++)
                if (span[i] == '\n') newlineCount++;

            int totalLines = newlineCount;
            if (span.Length > 0 && span[span.Length - 1] != '\n')
                totalLines++;
            int nDepths = totalLines / N_AZ_PER_CHANNEL;

            if (nDepths < 1)
                throw new InvalidDataException(
                    $"File {Path.GetFileName(path)} has fewer than {N_AZ_PER_CHANNEL} " +
                    "lines — not a valid azimuthal grid.");

            float[] depths = new float[nDepths];
            float[] azimuths = new float[N_AZ_PER_CHANNEL];
            float[,] values = new float[nDepths, N_AZ_PER_CHANNEL];

            for (int i = 0; i < N_AZ_PER_CHANNEL; i++)
                azimuths[i] = 1f + i * 2f;

            var inv = CultureInfo.InvariantCulture;
            const NumberStyles ns = NumberStyles.Float;

            int rowIdx = 0;
            int colIdx = 0;
            int idx = 0;
            int spanLen = span.Length;

            while (idx < spanLen && rowIdx < nDepths)
            {
                int lineStart = idx;
                int nlOffset = span.Slice(idx).IndexOf('\n');
                int lineEnd = (nlOffset < 0) ? spanLen : idx + nlOffset;

                int contentEnd = lineEnd;
                if (contentEnd > lineStart && span[contentEnd - 1] == '\r')
                    contentEnd--;

                int lineLen = contentEnd - lineStart;

                if (lineLen > 0)
                {
                    ReadOnlySpan<char> line = span.Slice(lineStart, lineLen);

                    int firstTab = line.IndexOf('\t');
                    if (firstTab < 0)
                        throw new InvalidDataException(
                            $"Malformed line at depth row {rowIdx}, col {colIdx}: " +
                            $"missing first tab.");

                    ReadOnlySpan<char> afterFirst = line.Slice(firstTab + 1);
                    int secondTabRel = afterFirst.IndexOf('\t');
                    if (secondTabRel < 0)
                        throw new InvalidDataException(
                            $"Malformed line at depth row {rowIdx}, col {colIdx}: " +
                            $"missing second tab.");

                    ReadOnlySpan<char> depthSpan = afterFirst.Slice(0, secondTabRel);
                    ReadOnlySpan<char> valueSpan = afterFirst.Slice(secondTabRel + 1);

                    if (colIdx == 0)
                        depths[rowIdx] = float.Parse(depthSpan, ns, inv);

                    float v;
                    if (valueSpan.IsEmpty || valueSpan.IsWhiteSpace())
                        v = NULL_VALUE_IN_Z_GRID;
                    else
                        v = float.Parse(valueSpan, ns, inv);

                    values[rowIdx, colIdx] = v;

                    colIdx++;
                    if (colIdx >= N_AZ_PER_CHANNEL)
                    {
                        colIdx = 0;
                        rowIdx++;
                    }
                }

                if (nlOffset < 0)
                    break;
                idx = lineEnd + 1;
            }

            return (depths, azimuths, values);
        }

        // =======================================================================
        // ConcatenateChannels — build the dual-contour grid. IDENTICAL to WPF.
        // =======================================================================
        private static void ConcatenateChannels(
            float[] depths,
            float[] xAwcn, float[,] zAwcn,
            float[] xTtcn, float[,] zTtcn,
            out float[] outDepths, out float[] outX, out float[,] outZ)
        {
            int N = depths.Length;
            int Aa = xAwcn.Length;
            int At = xTtcn.Length;
            int G = N_GAP_COLS;
            int totalCols = Aa + G + At;

            outDepths = depths;
            outX = new float[totalCols];
            outZ = new float[N, totalCols];

            for (int p = 0; p < Aa; p++)
                outX[p] = xAwcn[p];

            for (int p = 0; p < G; p++)
                outX[Aa + p] = 360.0f + 1.0f + p * 2.0f;

            for (int p = 0; p < At; p++)
                outX[Aa + G + p] = xTtcn[p] + TTCN_X_OFFSET;

            for (int s = 0; s < N; s++)
            {
                for (int p = 0; p < Aa; p++)
                    outZ[s, p] = zAwcn[s, p];

                for (int p = 0; p < G; p++)
                    outZ[s, Aa + p] = NULL_VALUE_IN_Z_GRID;

                for (int p = 0; p < At; p++)
                    outZ[s, Aa + G + p] = zTtcn[s, p];
            }
        }

        // =======================================================================
        // PeCustomGridNumber — relabel X-axis gridline numbers. IDENTICAL to WPF.
        // (ProEssentials chart event; args type and e.NumberString unchanged.)
        // =======================================================================
        private void Pesgo1_PeCustomGridNumber(
            object sender,
            Gigasoft.ProEssentials.EventArg.CustomGridNumberEventArgs e)
        {
            if (e.AxisType != 2) return;

            double v = e.NumberValue;

            if (v <= 360.5)
            {
                if (Math.Abs(v -   0) < 0.5) e.NumberString = "N";
                else if (Math.Abs(v -  90) < 0.5) e.NumberString = "E";
                else if (Math.Abs(v - 180) < 0.5) e.NumberString = "S";
                else if (Math.Abs(v - 270) < 0.5) e.NumberString = "W";
                else if (Math.Abs(v - 360) < 0.5) e.NumberString = "N";
                else e.NumberString = $"{v:0}\u00B0";
            }
            else if (v < TTCN_X_OFFSET - 0.5)
            {
                e.NumberString = "";
            }
            else
            {
                double localDeg = v - TTCN_X_OFFSET;
                e.NumberString = $"{localDeg:0}\u00B0";
            }
        }

        // WPF Window.Closing(CancelEventArgs) -> WinForms FormClosing
        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
        }
    }
}
