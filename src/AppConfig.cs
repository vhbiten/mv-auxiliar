using System;
using System.IO;

namespace SoulmvKit
{
    // Configuração central do app. Ajuste o Host se o servidor MV mudar de IP.
    public static class AppConfig
    {
        public const string AppName = "Auxiliar MV Soul";
        public const string Versao = "0.3.2";

        public const string HostPlaceholder = "http://mv-servidor.local";

        private static readonly object _hostLock = new object();
        private static string _host;

        // Endereco do servidor MV, resolvido uma vez, nesta ordem:
        //  1) variavel de ambiente MV_HOST (override rapido para testes);
        //  2) arquivo "config.txt" ao lado do exe, com a linha "host=http://IP"
        //     (config da maquina — e o jeito normal em producao);
        //  3) placeholder (nao conecta — sinaliza que falta configurar).
        // O config.txt NAO vai para o repositorio (esta no .gitignore).
        public static string Host
        {
            get
            {
                if (_host != null) return _host;
                lock (_hostLock)
                {
                    if (_host == null) _host = ResolverHost();
                    return _host;
                }
            }
        }

        // True quando nenhuma configuracao foi encontrada (o app usa o placeholder).
        public static bool HostConfigurado { get { return Host != HostPlaceholder; } }

        private static string ResolverHost()
        {
            try
            {
                var env = Environment.GetEnvironmentVariable("MV_HOST");
                if (!string.IsNullOrEmpty(env)) return env.Trim();
                string v = ValorConfig("host");
                if (v != null) return v;
            }
            catch (Exception ex) { Core.Logger.Log("AppConfig: falha ao resolver o host: " + ex.Message); }
            return HostPlaceholder;
        }

        // Lê "chave=valor" do config.txt. Aceita a linha solta (sem chave) como host,
        // que era o formato antigo — mas só para o host, senão uma linha de outra chave
        // seria confundida com o endereço do servidor.
        internal static string ValorConfig(string chave)
        {
            string cfg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");
            if (!File.Exists(cfg)) return null;
            foreach (var linha in File.ReadAllLines(cfg))
            {
                string s = linha.Trim();
                if (s.Length == 0 || s.StartsWith("#")) continue;
                int ig = s.IndexOf('=');
                if (ig > 0)
                {
                    if (!string.Equals(s.Substring(0, ig).Trim(), chave, StringComparison.OrdinalIgnoreCase)) continue;
                    string val = s.Substring(ig + 1).Trim();
                    if (val.Length > 0) return val;
                    continue;
                }
                if (chave == "host") return s;   // formato antigo: só a URL na linha
            }
            return null;
        }

        public const string CasApp   = "/mvautenticador-cas";
        public const string MvApp    = "/soul-mv";
        public const string FormsApp = "/soul-product-forms";

        // Form e item de menu da Produção de Kits (capturado do sistema real)
        public const string FormName = "M_PRODUZIR_KIT";
        public const string MenuId   = "MV.12.01.01.06.01.#";

        // Codigo da empresa/instituicao no MV
        public const int Company = 1;

        // Nome da máquina enviado nas mensagens Morphis (parâmetro MAQUINA)
        public static string Maquina { get { return Environment.MachineName; } }

        private static string _estacao;

        // Estação de trabalho para o PEP (/mvpep). Diferente do Morphis: ali o MV EXIGE
        // uma estação cadastrada — com um nome desconhecido ele cai no usuário técnico
        // ACESSOPRD e recusa a sessão. Normalmente o nome da máquina já serve; quando não
        // servir, ponha "estacao=NOME" no config.txt.
        public static string Estacao
        {
            get
            {
                if (_estacao != null) return _estacao;
                var env = Environment.GetEnvironmentVariable("MV_ESTACAO");
                if (!string.IsNullOrEmpty(env)) return _estacao = env.Trim();
                try { string v = ValorConfig("estacao"); if (v != null) return _estacao = v; }
                catch (Exception ex) { Core.Logger.Log("AppConfig: falha ao ler a estação: " + ex.Message); }
                return _estacao = Environment.MachineName;
            }
        }
    }
}
