using System;

namespace SoulmvKit
{
    // Configuração central do app. Ajuste o Host se o servidor MV mudar de IP.
    public static class AppConfig
    {
        public const string AppName = "Auxiliar MV Soul";
        public const string Versao = "0.1.0";

        // Endereco do servidor MV. Definido via variavel de ambiente MV_HOST
        // (ex.: setx MV_HOST "http://ip-do-servidor"). O placeholder abaixo e usado
        // apenas quando a variavel nao esta definida.
        public static string Host
        {
            get
            {
                var h = Environment.GetEnvironmentVariable("MV_HOST");
                return string.IsNullOrEmpty(h) ? "http://mv-servidor.local" : h;
            }
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
    }
}
