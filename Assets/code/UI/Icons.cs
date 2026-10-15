using System;
using Prowl.PaperUI;
using Prowl.Quill;
using Prowl.Vector;

namespace Slopbox;

/// <summary>The icons DXRP takes from Material Icons, redrawn as vectors since Prowl has no icon font.</summary>
public enum Icon
{
    Play, Settings, Logout, Delete, Refresh, Cube, Shapes, Light, Construction, Bolt, Undo, Close, Check, Info, Search, Speed,
}

public static class Icons
{
    /// <summary>Declares a fixed size box showing <paramref name="icon"/>.</summary>
    public static void Box(Paper paper, Icon icon, float size, Color color, int id = 0)
    {
        using (paper.Box("icon", id).Width(size).Height(size).IsNotInteractable().Enter())
            paper.Draw((canvas, rect) => Draw(canvas, icon, rect.Min.X + rect.Size.X * 0.5f, rect.Min.Y + rect.Size.Y * 0.5f, size, color));
    }

    /// <summary>Draws <paramref name="icon"/> centred on (<paramref name="cx"/>, <paramref name="cy"/>), <paramref name="size"/> pixels across.</summary>
    public static void Draw(Canvas c, Icon icon, float cx, float cy, float size, Color color)
    {
        float s = size / 24f;   // icons are designed on a 24px grid, like Material's
        float stroke = MathF.Max(1.5f, 2f * s);

        void Stroke()
        {
            c.SetStrokeColor(color);
            c.SetStrokeWidth(stroke);
            c.Stroke();
        }

        void Line(float x0, float y0, float x1, float y1)
        {
            c.BeginPath();
            c.MoveTo(cx + x0 * s, cy + y0 * s);
            c.LineTo(cx + x1 * s, cy + y1 * s);
            Stroke();
        }

        void Polyline(bool fill, params float[] p)
        {
            c.BeginPath();
            c.MoveTo(cx + p[0] * s, cy + p[1] * s);
            for (int i = 2; i < p.Length; i += 2) c.LineTo(cx + p[i] * s, cy + p[i + 1] * s);
            if (fill)
            {
                c.ClosePath();
                c.SetFillColor(color);
                c.Fill();
            }
            else Stroke();
        }

        void Arc(float r, float from, float to)
        {
            c.BeginPath();
            c.Arc(cx, cy, r * s, from, to);
            Stroke();
        }

        switch (icon)
        {
            case Icon.Play:
                c.CircleFilled(cx, cy, 10f * s, color);
                c.BeginPath();
                c.MoveTo(cx - 3f * s, cy - 5f * s);
                c.LineTo(cx + 5f * s, cy);
                c.LineTo(cx - 3f * s, cy + 5f * s);
                c.ClosePath();
                c.SetFillColor(Theme.Secondary);
                c.Fill();
                break;

            case Icon.Settings:
                for (int i = 0; i < 8; i++)
                {
                    float a = i * MathF.PI / 4f;
                    float x = MathF.Cos(a), y = MathF.Sin(a), px = -y, py = x;
                    // A tooth: a short rectangle pointing out from the ring.
                    Polyline(true,
                        x * 6f + px * 2f, y * 6f + py * 2f, x * 10f + px * 2f, y * 10f + py * 2f,
                        x * 10f - px * 2f, y * 10f - py * 2f, x * 6f - px * 2f, y * 6f - py * 2f);
                }
                Arc(5.5f, 0f, MathF.PI * 2f);
                c.CircleFilled(cx, cy, 2.5f * s, color);
                break;

            case Icon.Logout:
                Polyline(false, -2f, -9f, -9f, -9f, -9f, 9f, -2f, 9f);
                Line(-3f, 0f, 9f, 0f);
                Polyline(false, 5f, -4f, 9f, 0f, 5f, 4f);
                break;

            case Icon.Delete:
                Line(-8f, -6f, 8f, -6f);
                Polyline(false, -3f, -6f, -3f, -9f, 3f, -9f, 3f, -6f);
                Polyline(false, -6f, -6f, -5f, 9f, 5f, 9f, 6f, -6f);
                Line(-1.5f, -2f, -1.5f, 5f);
                Line(1.5f, -2f, 1.5f, 5f);
                break;

            case Icon.Refresh:
                Arc(8f, -MathF.PI * 0.35f, MathF.PI * 1.5f);
                Polyline(true, 3f, -10f, 9f, -8f, 4f, -3f);
                break;

            case Icon.Undo:
                c.BeginPath();
                c.Arc(cx + 1f * s, cy + 2f * s, 7f * s, -MathF.PI * 0.5f, MathF.PI * 0.5f);
                Stroke();
                Line(1f, -5f, -7f, -5f);
                Polyline(false, -3f, -9f, -7f, -5f, -3f, -1f);
                break;

            case Icon.Cube:
                Polyline(false, 0f, -9f, 8f, -5f, 8f, 5f, 0f, 9f, -8f, 5f, -8f, -5f, 0f, -9f);
                Polyline(false, -8f, -5f, 0f, -1f, 8f, -5f);
                Line(0f, -1f, 0f, 9f);
                break;

            case Icon.Shapes:
                c.CircleFilled(cx - 3f * s, cy - 3f * s, 5.5f * s, color);
                c.RectFilled(cx + 0f * s, cy + 0f * s, 9f * s, 9f * s, color);
                break;

            case Icon.Light:
                Arc(6f, MathF.PI * 0.8f, MathF.PI * 2.2f);
                Polyline(false, -4f, 3.5f, -3f, 6f, 3f, 6f, 4f, 3.5f);
                Line(-2.5f, 9f, 2.5f, 9f);
                break;

            case Icon.Construction:
                // A wrench: a handle and an open jaw.
                Line(-7f, 7f, 2f, -2f);
                c.BeginPath();
                c.Arc(cx + 4.5f * s, cy - 4.5f * s, 4.5f * s, MathF.PI * 0.95f, MathF.PI * 2.55f);
                Stroke();
                break;

            case Icon.Bolt:
                Polyline(true, 2f, -10f, -6f, 2f, 0f, 2f, -2f, 10f, 6f, -2f, 0f, -2f);
                break;

            case Icon.Close:
                Line(-6f, -6f, 6f, 6f);
                Line(6f, -6f, -6f, 6f);
                break;

            case Icon.Check:
                Polyline(false, -7f, 0f, -2f, 5f, 8f, -6f);
                break;

            case Icon.Info:
                Arc(9f, 0f, MathF.PI * 2f);
                Line(0f, -1f, 0f, 5f);
                c.CircleFilled(cx, cy - 4.5f * s, 1.3f * s, color);
                break;

            case Icon.Search:
                c.BeginPath();
                c.Arc(cx - 2f * s, cy - 2f * s, 6f * s, 0f, MathF.PI * 2f);
                Stroke();
                Line(2.5f, 2.5f, 8f, 8f);
                break;

            case Icon.Speed:
                Arc(9f, MathF.PI * 0.85f, MathF.PI * 2.15f);
                Line(0f, 2f, 5f, -4f);
                c.CircleFilled(cx, cy + 2f * s, 2f * s, color);
                break;
        }
    }
}
