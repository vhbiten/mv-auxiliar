using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Diálogo modal no visual do app (no lugar do MessageBox nativo): cartão branco
    // com barra de acento no topo, ícone em disco, título, texto e botão primário.
    // Cantos arredondados via DWM no Windows 11 (no 10 fica reto, sem erro).
    public class StyledDialog : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private readonly StatusKind _kind;
        private readonly string _titulo;
        private readonly string _mensagem;
        private readonly float _sc;          // fator de DPI (96 = 1.0)
        private Rectangle _rcTitulo, _rcMsg, _rcDisco;

        private StyledDialog(string titulo, string mensagem, StatusKind kind, string textoBotao)
        {
            _titulo = titulo; _mensagem = mensagem; _kind = kind;

            this.AutoScaleMode = AutoScaleMode.None;   // layout escalado à mão (pinta e posiciona junto)
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.CenterParent;
            this.ShowInTaskbar = false;
            this.BackColor = Theme.CardBg;
            this.Font = Theme.Body;
            this.DoubleBuffered = true;

            using (var g = this.CreateGraphics()) _sc = g.DpiX / 96f;

            int W = S(470);
            int xTexto = S(66);
            int wTexto = W - xTexto - S(24);

            Size szTit = TextRenderer.MeasureText(_titulo, Theme.H2, new Size(wTexto, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            Size szMsg = TextRenderer.MeasureText(_mensagem, Theme.Body, new Size(wTexto, 0),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

            int yTit = S(28);
            _rcTitulo = new Rectangle(xTexto, yTit, wTexto, szTit.Height);
            _rcMsg = new Rectangle(xTexto, yTit + szTit.Height + S(10), wTexto, szMsg.Height);
            _rcDisco = new Rectangle(S(24), yTit + (szTit.Height - S(26)) / 2, S(26), S(26));

            var btn = new PrimaryButton();
            btn.Text = textoBotao;
            btn.Size = new Size(S(170), S(38));
            btn.Location = new Point(W - S(24) - btn.Width, _rcMsg.Bottom + S(24));
            btn.Click += new EventHandler(delegate { this.DialogResult = DialogResult.OK; this.Close(); });
            this.Controls.Add(btn);
            this.AcceptButton = btn;
            this.CancelButton = btn;

            this.ClientSize = new Size(W, btn.Bottom + S(22));
        }

        private int S(int v) { return (int)Math.Round(v * _sc); }

        // Aviso modal (uma opção só). Bloqueia até o usuário confirmar.
        public static void Aviso(IWin32Window dono, string titulo, string mensagem, string textoBotao)
        {
            using (var d = new StyledDialog(titulo, mensagem, StatusKind.Warning, textoBotao))
                d.ShowDialog(dono);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Windows 11: cantos arredondados (DWMWA_WINDOW_CORNER_PREFERENCE = ROUND)
            try { int v = 2; DwmSetWindowAttribute(this.Handle, 33, ref v, 4); } catch { }
        }

        // Sem barra de título: deixa arrastar segurando qualquer área vazia do cartão.
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84)   // WM_NCHITTEST
            {
                base.WndProc(ref m);
                if ((int)m.Result == 1) m.Result = (IntPtr)2;   // HTCLIENT -> HTCAPTION
                return;
            }
            base.WndProc(ref m);
        }

        private void Palette(out Color strong, out string glyph)
        {
            switch (_kind)
            {
                case StatusKind.Success: strong = Theme.Success; glyph = "✓"; break;
                case StatusKind.Error:   strong = Theme.Error;   glyph = "×"; break;
                case StatusKind.Warning: strong = Theme.Warning; glyph = "!"; break;
                default:                 strong = Theme.Primary; glyph = "i"; break;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;

            Color strong; string glyph;
            Palette(out strong, out glyph);

            // barra de acento no topo + moldura
            using (var br = new SolidBrush(strong))
                g.FillRectangle(br, 0, 0, this.Width, S(4));
            using (var pen = new Pen(Theme.Border))
                g.DrawRectangle(pen, 0, 0, this.Width - 1, this.Height - 1);

            // ícone em disco (igual ao StatusBanner)
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var br = new SolidBrush(strong))
                g.FillEllipse(br, _rcDisco);
            TextRenderer.DrawText(g, glyph, Theme.BodyBold, _rcDisco, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            TextRenderer.DrawText(g, _titulo, Theme.H2, _rcTitulo, Theme.TextDark,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, _mensagem, Theme.Body, _rcMsg, Theme.TextDark,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
    }
}
