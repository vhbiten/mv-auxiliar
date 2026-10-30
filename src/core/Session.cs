using System;
using System.Net.Http;

namespace SoulmvKit.Core
{
    // Sessão autenticada: o HttpClient (com cookies) é reaproveitado pelo MorphisClient.
    public class Session
    {
        public readonly HttpClient Http;
        public readonly string Host;
        public readonly string User;
        // Cookies da sessão (o JSESSIONID do forms é a identidade dela no servidor). A reserva
        // da aba Atender os guarda protegidos em disco para liberar a solicitação depois de uma
        // queda do programa (ReservaAtendimento).
        public System.Net.CookieContainer Cookies;
        public string NomeCompleto;   // nome completo do usuário (do contextInfo); pode ser null
        public int Empresa = AppConfig.Company;   // empresa do login (refeita na renovação)

        // Estado mostrado em Configurações > Sessão.
        public readonly DateTime ConectadoEm = DateTime.Now;
        public DateTime? UltimoKeepalive;    // última renovação de inatividade bem-sucedida
        public DateTime? UltimaRenovacao;    // última reconexão sem senha (SSO)

        public Session(HttpClient http, string host, string user)
        {
            Http = http;
            Host = host;
            User = user;
        }
    }
}
