using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SoulmvKit.UI
{
    // Moldura das janelas da aba Atender (lista do F9, conferência), igual à do CaixasDialog:
    // faixa azul no topo, borda, sombra, fundo escurecido atrás, arrastar pelo título e ✕.
    // Tamanho fixo de quem abre (a 96 DPI), só reduzido se a janela do app for menor.
    public class MolduraDialog : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected readonly float _sc;
        private readonly Size _tamanho;
        private Form _overlay;

        public MolduraDialog(Size tamanho)
        {
            _tamanho = tamanho;
            this.AutoScaleMode = AutoScaleMode.None;
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.Manual;
            this.ShowInTaskbar = false;
            this.BackColor = Theme.CardBg;
            this.Font = Theme.Body;
            this.DoubleBuffered = true;
            this.KeyPreview = true;

            using (var g = this.CreateGraphics()) _sc = g.DpiX / 96f;
            this.Padding = new Padding(S(1), S(5), S(1), S(1));
            this.FormClosed += new FormClosedEventHandler(delegate
            {
                if (_overlay != null) { try { _overlay.Close(); _overlay.Dispose(); } catch { } _overlay = null; }
            });
        }

        protected int S(int v) { return (int)Math.Round(v * _sc); }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ClassStyle |= 0x00020000 /* CS_DROPSHADOW */; return cp; }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            var dono = this.Owner;
            int w = S(_tamanho.Width), h = S(_tamanho.Height);
            if (dono != null && dono.Visible && dono.Width > 0)
            {
                w = Math.Min(w, dono.Width - S(40));
                h = Math.Min(h, dono.Height - S(40));
            }
            this.Size = new Size(w, h);
            if (dono != null)
                this.Location = new Point(dono.Left + (dono.Width - w) / 2, dono.Top + (dono.Height - h) / 2);
            else
            {
                var wa = Screen.FromControl(this).WorkingArea;
                this.Location = new Point(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2);
            }
        }

        // Título, subtítulo e ✕. Quem herda adiciona o corpo (Fill) e depois este painel (Top).
        protected Panel Cabecalho(string titulo, string subtitulo)
        {
            var pnl = new Panel();
            pnl.Dock = DockStyle.Top;
            pnl.Height = S(string.IsNullOrEmpty(subtitulo) ? 54 : 76);
            pnl.BackColor = Theme.CardBg;

            var lblTitulo = new Label();
            lblTitulo.Text = titulo;
            lblTitulo.Font = Theme.H2; lblTitulo.ForeColor = Theme.TextDark; lblTitulo.BackColor = Theme.CardBg;
            lblTitulo.AutoSize = true; lblTitulo.Location = new Point(S(24), S(14));
            pnl.Controls.Add(lblTitulo);

            if (!string.IsNullOrEmpty(subtitulo))
            {
                var lblSub = new Label();
                lblSub.Text = subtitulo;
                lblSub.Font = Theme.Body; lblSub.ForeColor = Theme.TextSoft; lblSub.BackColor = Theme.CardBg;
                lblSub.AutoSize = true; lblSub.Location = new Point(S(25), S(46));
                pnl.Controls.Add(lblSub);
            }

            var btnX = new Label();
            btnX.Text = "✕";
            btnX.Font = new Font(Theme.Body.FontFamily, 11f, FontStyle.Regular);
            btnX.ForeColor = Theme.TextSoft; btnX.BackColor = Theme.CardBg;
            btnX.TextAlign = ContentAlignment.MiddleCenter;
            btnX.Size = new Size(S(44), S(32));
            btnX.Location = new Point(pnl.Width - S(46), S(2));
            btnX.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnX.Cursor = Cursors.Hand;
            btnX.MouseEnter += new EventHandler(delegate { btnX.BackColor = Theme.Error; btnX.ForeColor = Color.White; });
            btnX.MouseLeave += new EventHandler(delegate { btnX.BackColor = Theme.CardBg; btnX.ForeColor = Theme.TextSoft; });
            btnX.Click += new EventHandler(delegate { Close(); });
            pnl.Controls.Add(btnX);
            return pnl;
        }

        // ===== arrastar pelo título, sombra e borda =====
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84)   // WM_NCHITTEST
            {
                base.WndProc(ref m);
                int lp = m.LParam.ToInt32();
                var pt = this.PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                if (pt.Y >= S(8) && pt.Y <= S(44) && pt.X < this.ClientSize.Width - S(60)) m.Result = (IntPtr)2;   // HTCAPTION
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int v = 2; DwmSetWindowAttribute(this.Handle, 33, ref v, 4); } catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var br = new SolidBrush(Theme.Primary))
                e.Graphics.FillRectangle(br, 0, 0, this.Width, S(4));
            using (var pen = new Pen(Theme.Border))
                e.Graphics.DrawRectangle(pen, 0, 0, this.Width - 1, this.Height - 1);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            var dono = this.Owner;
            if (dono != null && dono.Visible)
            {
                _overlay = new OverlayEscurecido(dono);
                _overlay.Show();
                this.BringToFront();
                this.Activate();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel) return;
            // remove o overlay ANTES da destruição e devolve o foco (senão o Windows minimiza o app)
            if (_overlay != null) { try { _overlay.Hide(); _overlay.Close(); _overlay.Dispose(); } catch { } _overlay = null; }
            var dono = this.Owner;
            if (dono != null) { try { dono.Activate(); } catch { } }
        }
    }
}
