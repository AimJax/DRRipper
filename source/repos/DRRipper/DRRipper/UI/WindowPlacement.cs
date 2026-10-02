using System;
using System.Collections.Generic;

namespace DRRipper.UI
{
    /// <summary>
    /// Window placement capture/normalization (Ticket #006.1 §17). Pure logic —
    /// no windows, no screens API here; the view supplies work areas. A minimized
    /// state is never persisted or restored. Off-screen placements recenter.
    /// </summary>
    public sealed class WindowPlacement
    {
        public double Width { get; set; } = 1100;
        public double Height { get; set; } = 640;
        public double Left { get; set; } = double.NaN;
        public double Top { get; set; } = double.NaN;
        public bool Maximized { get; set; }

        public static WindowPlacement FromSettings(AppSettings settings)
        {
            return new WindowPlacement
            {
                Width = settings.WindowWidth,
                Height = settings.WindowHeight,
                Left = settings.WindowLeft,
                Top = settings.WindowTop,
                Maximized = settings.WindowMaximized,
            };
        }

        public void ApplyTo(AppSettings settings)
        {
            settings.WindowWidth = Width;
            settings.WindowHeight = Height;
            settings.WindowLeft = Left;
            settings.WindowTop = Top;
            settings.WindowMaximized = Maximized;
        }

        /// <summary>Work area supplied by the view (pixels, any coordinate space).</summary>
        public readonly struct WorkArea
        {
            public readonly double Left, Top, Width, Height;
            public WorkArea(double left, double top, double width, double height)
            {
                Left = left; Top = top; Width = width; Height = height;
            }
        }

        /// <summary>
        /// Normalizes for restore: clamps size, drops minimized, recenters fully
        /// off-screen windows onto the primary work area. Never throws.
        /// </summary>
        public WindowPlacement Normalized(IReadOnlyList<WorkArea> areas)
        {
            var result = new WindowPlacement
            {
                Width = Math.Clamp(Width, 640, 7680),
                Height = Math.Clamp(Height, 400, 4320),
                Left = Left,
                Top = Top,
                Maximized = Maximized,
            };
            if (areas == null || areas.Count == 0)
            {
                result.Left = double.NaN;
                result.Top = double.NaN;
                return result;
            }
            if (double.IsNaN(result.Left) || double.IsNaN(result.Top))
            {
                return result; // no stored position: OS default placement
            }
            foreach (var a in areas)
            {
                // Visible if any part overlaps a work area (with tolerance).
                bool overlaps =
                    result.Left < a.Left + a.Width - 50 && result.Left + result.Width > a.Left + 50 &&
                    result.Top < a.Top + a.Height - 50 && result.Top + result.Height > a.Top + 50;
                if (overlaps) return result;
            }
            // Fully off-screen (monitor unplugged): recenter on the primary area.
            var primary = areas[0];
            result.Left = primary.Left + Math.Max(0, (primary.Width - result.Width) / 2);
            result.Top = primary.Top + Math.Max(0, (primary.Height - result.Height) / 2);
            return result;
        }
    }
}
