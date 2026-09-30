using System;
using System.Drawing;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Moldura reta para um campo (ex.: TextBox/NumericUpDown sem borda dentro),
    // dando a ele o mesmo visual flat/bordado dos demais campos. Acende a borda
    // em azul quando o campo interno ganha foco (fiação automática: qualquer
    // filho adicionado tem GotFocus/LostFocus observados).
    public class FieldHost : Panel
    {
        public int Radius = 0;   // mantido por compatibilidade; o desenho é reto

        private bool _focado;

        public FieldHost()
        {
            this.DoubleBuffered = true;
            this.BackColor = Color.White;
            this.Padding = new Padding(Theme.S(9), Theme.S(6), Theme.S(6), Theme.S(6));
            // Mesmo motivo do Card: a borda é desenhada em Width-1 e, sem o redraw no
            // resize, a borda da largura antiga ficava para trás como risco.
            this.SetStyle(ControlStyles.ResizeRedraw, true);
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            Observar(e.Control);
        }

        private void Observar(Control c)
        {
            c.GotFocus += new EventHandler(delegate { _focado = true; this.Invalidate(); });
            c.LostFocus += new EventHandler(delegate { _focado = false; this.Invalidate(); });
            c.ControlAdded += new ControlEventHandler(delegate(object s, ControlEventArgs ee) { Observar(ee.Control); });
            foreach (Control filho in c.Controls) Observar(filho);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var br = new SolidBrush(Color.White))
                g.FillRectangle(br, this.ClientRectangle);
            if (_focado)
            {
                using (var pen = new Pen(Theme.Primary, 2f))
                    g.DrawRectangle(pen, 1, 1, this.Width - 3, this.Height - 3);
            }
            else
            {
                using (var pen = new Pen(Theme.Border))
                    g.DrawRectangle(pen, 0, 0, this.Width - 1, this.Height - 1);
            }
        }
    }
}
