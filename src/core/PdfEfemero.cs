using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SoulmvKit.Core
{
    // Entrega um PDF ao navegador sem escrever nada em disco.
    //
    // O PEP faz isso com um POST de formulário abrindo _blank: quem busca o documento é o
    // próprio navegador, e nada sobra na máquina. Um app WinForms não consegue passar um
    // POST para o navegador pela linha de comando, então fazemos o equivalente — um socket
    // de vida curta em 127.0.0.1 que serve os bytes que já estão na memória e se desliga
    // sozinho. Ninguém de fora alcança (loopback) e nem outro processo local adivinha a
    // URL, porque o caminho carrega um token aleatório.
    //
    // TcpListener e não HttpListener de propósito: o http.sys exige reserva de URL
    // (netsh urlacl) para usuário sem privilégio, e um socket comum não exige nada.
    public static class PdfEfemero
    {
        private const int MinutosDeVida = 3;      // depois disso a aba recarregada dá erro — e o PDF some da memória
        private const int TetoDoPedido = 8192;

        // ATENÇÃO: o vetor recebido é ZERADO quando o servidor encerra. Quem chama entrega
        // e esquece — é o que mantém o dado de paciente fora da memória do processo.
        public static string Publicar(byte[] pdf, string nome)
        {
            var ouvinte = new TcpListener(IPAddress.Loopback, 0);
            ouvinte.Start();
            int porta = ((IPEndPoint)ouvinte.LocalEndpoint).Port;
            string caminho = "/" + Guid.NewGuid().ToString("N") + "/" + nome;

            var fio = new Thread(delegate () { Servir(ouvinte, caminho, nome, pdf); });
            fio.IsBackground = true;
            fio.Start();

            return "http://127.0.0.1:" + porta + caminho;
        }

        private static void Servir(TcpListener ouvinte, string caminho, string nome, byte[] pdf)
        {
            DateTime prazo = DateTime.Now.AddMinutes(MinutosDeVida);
            try
            {
                while (DateTime.Now < prazo)
                {
                    if (!ouvinte.Pending()) { Thread.Sleep(40); continue; }
                    using (var cliente = ouvinte.AcceptTcpClient())
                    {
                        cliente.ReceiveTimeout = 5000;
                        cliente.SendTimeout = 20000;
                        using (var fluxo = cliente.GetStream())
                        {
                            string pedido = LerPedido(fluxo);
                            bool nosso = pedido != null && pedido.IndexOf(" " + caminho + " ", StringComparison.Ordinal) >= 0;
                            if (nosso) Responder(fluxo, nome, pdf);
                            else Recusar(fluxo);
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.Log("PDF efêmero: " + ex.Message); }
            finally
            {
                try { ouvinte.Stop(); } catch { }
                Array.Clear(pdf, 0, pdf.Length);
            }
        }

        // Lê só o cabeçalho do pedido; o corpo (se houver) não interessa.
        private static string LerPedido(NetworkStream fluxo)
        {
            var buf = new byte[TetoDoPedido];
            int total = 0;
            try
            {
                while (total < buf.Length)
                {
                    int lido = fluxo.Read(buf, total, buf.Length - total);
                    if (lido <= 0) break;
                    total += lido;
                    string ate = Encoding.ASCII.GetString(buf, 0, total);
                    if (ate.IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0) return ate;
                    if (ate.IndexOf("\n\n", StringComparison.Ordinal) >= 0) return ate;
                }
            }
            catch (Exception ex) { Logger.Log("PDF efêmero: leitura - " + ex.Message); }
            return total > 0 ? Encoding.ASCII.GetString(buf, 0, total) : null;
        }

        private static void Responder(NetworkStream fluxo, string nome, byte[] pdf)
        {
            string cab = "HTTP/1.1 200 OK\r\n"
                       + "Content-Type: application/pdf\r\n"
                       + "Content-Length: " + pdf.Length + "\r\n"
                       + "Content-Disposition: inline; filename=\"" + nome + "\"\r\n"
                       + "Cache-Control: no-store\r\n"
                       + "X-Content-Type-Options: nosniff\r\n"
                       + "Connection: close\r\n\r\n";
            byte[] b = Encoding.ASCII.GetBytes(cab);
            fluxo.Write(b, 0, b.Length);
            fluxo.Write(pdf, 0, pdf.Length);
            fluxo.Flush();
        }

        private static void Recusar(NetworkStream fluxo)
        {
            byte[] b = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            fluxo.Write(b, 0, b.Length);
            fluxo.Flush();
        }

        // Versões anteriores gravavam o PDF em %TEMP%. Se sobrou algo, some com isso.
        public static void LimparResiduos()
        {
            try
            {
                string pasta = Path.Combine(Path.GetTempPath(), "mv-auxiliar-justificativas");
                if (Directory.Exists(pasta)) Directory.Delete(pasta, true);
            }
            catch (Exception ex) { Logger.Log("PDF efêmero: resíduo - " + ex.Message); }
        }
    }
}
