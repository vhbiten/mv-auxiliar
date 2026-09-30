using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace SoulmvKit.Core
{
    // Controle de acesso por chave: cada instalação tem um licenca.txt ao lado do exe
    // e o GitHub publica só os HASHES autorizados (asset "licencas.txt" da release fixa
    // "licencas" — gerida pelo licenca.ps1 na raiz do projeto). Tirar o hash da lista
    // bloqueia a instalação na abertura seguinte (e na revalidação de 6 em 6 horas).
    //
    // Sem internet vale a última confirmação por até CarenciaDias (o hospital não pode
    // parar porque a rede caiu); vencida a carência, bloqueia até conseguir validar.
    // É controle administrativo de quem pode usar — não resiste a engenharia reversa
    // do exe, e não precisa: o objetivo é revogar remotamente instalações entregues.
    public static class Licenca
    {
        private const string Url = "https://github.com/vhbiten/mv-auxiliar/releases/download/licencas/licencas.txt";
        private const int CarenciaDias = 7;

        public enum Estado
        {
            Ok,             // hash na lista (ou offline dentro da carência)
            SemChave,       // não existe licenca.txt ao lado do exe
            Revogada,       // a lista veio do servidor e a chave não está nela
            ForaDaCarencia  // sem conseguir validar há mais de CarenciaDias
        }

        public static async Task<Estado> ValidarAsync()
        {
            string chave = LerChaveLocal();
            if (chave == null) return Estado.SemChave;
            string hash = Sha256Hex(chave);
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(15);
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("mv-auxiliar");
                    // O CDN do GitHub segura o asset antigo por vários minutos na URL limpa;
                    // a query única fura o cache — revogação tem de valer na hora.
                    string lista = await http.GetStringAsync(Url + "?t=" + DateTime.UtcNow.Ticks);
                    foreach (var linha in lista.Split('\n'))
                    {
                        string s = linha.Trim();
                        if (s.Length == 0 || s.StartsWith("#")) continue;
                        int fim = s.IndexOfAny(new[] { ' ', '\t' });
                        string h = fim > 0 ? s.Substring(0, fim) : s;
                        if (string.Equals(h, hash, StringComparison.OrdinalIgnoreCase))
                        {
                            GravarCarimbo(hash);
                            return Estado.Ok;
                        }
                    }
                    // A lista veio e a chave está fora: revogação de verdade. O carimbo
                    // morre junto, senão ficar offline reganharia a carência.
                    ApagarCarimbo();
                    Logger.Log("Licença: chave desta instalação não está mais autorizada.");
                    return Estado.Revogada;
                }
            }
            catch (Exception ex)
            {
                DateTime ultima;
                if (LerCarimbo(hash, out ultima) && (DateTime.Now - ultima).TotalDays <= CarenciaDias)
                    return Estado.Ok;
                Logger.Log("Licença: sem validar há mais de " + CarenciaDias + " dias (" + ex.Message + ").");
                return Estado.ForaDaCarencia;
            }
        }

        public static string MensagemTitulo(Estado e)
        {
            switch (e)
            {
                case Estado.SemChave: return "Licença não encontrada";
                case Estado.Revogada: return "Acesso desativado";
                default: return "Licença não confirmada";
            }
        }

        public static string MensagemTexto(Estado e)
        {
            switch (e)
            {
                case Estado.SemChave:
                    return "Este computador está sem o arquivo de licença (licenca.txt).\n" +
                           "Copie-o para a pasta do programa e clique em Verificar novamente.";
                case Estado.Revogada:
                    return "O acesso desta instalação foi desativado.\n" +
                           "Fale com o responsável pelo programa.";
                default:
                    return "Não foi possível confirmar a licença há mais de " + CarenciaDias + " dias.\n" +
                           "Conecte à internet e clique em Verificar novamente.";
            }
        }

        // licenca.txt ao lado do exe: a chave, numa linha (espaços e caixa não importam).
        private static string LerChaveLocal()
        {
            try
            {
                string arq = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "licenca.txt");
                if (!File.Exists(arq)) return null;
                string s = File.ReadAllText(arq).Trim().ToUpperInvariant().Replace(" ", "");
                return s.Length == 0 ? null : s;
            }
            catch { return null; }
        }

        private static string Sha256Hex(string texto)
        {
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder();
                foreach (byte b in sha.ComputeHash(Encoding.UTF8.GetBytes(texto)))
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        // Carimbo da última validação online OK ("hash|data"), em dados\. Serve só para
        // a carência offline; preso ao hash para não valer depois de trocar a chave.
        private static string CaminhoCarimbo()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dados", "licenca-carimbo.txt");
        }

        private static void GravarCarimbo(string hash)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CaminhoCarimbo()));
                File.WriteAllText(CaminhoCarimbo(), hash + "|" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch { }
        }

        private static bool LerCarimbo(string hash, out DateTime quando)
        {
            quando = DateTime.MinValue;
            try
            {
                if (!File.Exists(CaminhoCarimbo())) return false;
                var partes = File.ReadAllText(CaminhoCarimbo()).Trim().Split('|');
                if (partes.Length != 2 || !string.Equals(partes[0], hash, StringComparison.OrdinalIgnoreCase)) return false;
                return DateTime.TryParse(partes[1], out quando);
            }
            catch { return false; }
        }

        private static void ApagarCarimbo()
        {
            try { if (File.Exists(CaminhoCarimbo())) File.Delete(CaminhoCarimbo()); } catch { }
        }
    }
}
