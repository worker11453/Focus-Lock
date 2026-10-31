using System;
using System.Drawing;
using System.Windows.Forms;

namespace FocusLock
{
    internal static class Ui
    {
        public static readonly Color Bg = Color.FromArgb(18, 20, 23);
        public static readonly Color Card = Color.FromArgb(27, 30, 35);
        public static readonly Color Card2 = Color.FromArgb(34, 38, 45);
        public static readonly Color Line = Color.FromArgb(46, 51, 59);
        public static readonly Color Text = Color.FromArgb(232, 234, 237);
        public static readonly Color Muted = Color.FromArgb(154, 160, 166);
        public static readonly Color Red = Color.FromArgb(229, 72, 77);
        public static readonly Color Green = Color.FromArgb(46, 158, 91);
        public static readonly Color Yellow = Color.FromArgb(226, 179, 64);

        public static Font Font(float size)
        {
            return new Font("Microsoft YaHei UI", size, FontStyle.Regular, GraphicsUnit.Point);
        }

        public static Font FontBold(float size)
        {
            return new Font("Microsoft YaHei UI", size, FontStyle.Bold, GraphicsUnit.Point);
        }

        public static Label MakeLabel(string text, float size, Color color, bool bold)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.BackColor = Color.Transparent;
            l.ForeColor = color;
            l.Font = bold ? FontBold(size) : Font(size);
            return l;
        }

        public static Button MakeButton(string text, int x, int y, int w, int h)
        {
            Button b = new Button();
            b.Text = text;
            b.Bounds = new Rectangle(x, y, w, h);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Line;
            b.FlatAppearance.MouseOverBackColor = Card2;
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(45, 50, 58);
            b.BackColor = Card;
            b.ForeColor = Text;
            b.Font = Font(10f);
            b.Cursor = Cursors.Hand;
            b.TextAlign = ContentAlignment.MiddleLeft;
            b.Padding = new Padding(14, 0, 0, 0);
            return b;
        }

        public static void Center(Control c, Control parent)
        {
            c.Left = (parent.ClientSize.Width - c.Width) / 2;
        }
    }

}
