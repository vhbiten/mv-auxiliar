using System;
using System.Text.RegularExpressions;

namespace SoulmvKit.Core
{
    // Turno ("horário") das solicitações que vêm das prescrições. O MV gera um pedido por
    // turno e carimba o turno na observação (DS_OBS), sempre junto com a hora cheia dele:
    //   "MADRUGADA 00:01/06:00", "MANHÃ 06:01/13:00" (também vem "MANHA"),
    //   "TARDE 13:01/18:00" e "NOITE 18:01/00:00".
    // Medido em 28/11/2026 em 429 solicitações de dois dias: toda linha com o carimbo tem a
    // hora do turno e vice-versa; a maioria é de prescrição (PRE), algumas de transferência
    // (TRA). Pedido imediato (pronto atendimento, urgente, radioterapia) leva a hora real e
    // não tem turno — por isso o carimbo é quem manda, e não a hora: um pedido imediato feito
    // às 13:01 não é do turno da tarde.
    public static class Turnos
    {
        public const string Madrugada = "D", Manha = "M", Tarde = "T", Noite = "N";

        private static readonly Regex Carimbo = new Regex(
            "^\\s*(MADRUGADA|MANH.|TARDE|NOITE)\\s+\\d{2}:\\d{2}\\s*/\\s*\\d{2}:\\d{2}\\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Turno escrito na observação do pedido; "" quando ela não é o carimbo de turno.
        public static string DoCarimbo(string obs)
        {
            if (string.IsNullOrEmpty(obs)) return "";
            var m = Carimbo.Match(obs);
            if (!m.Success) return "";
            string nome = m.Groups[1].Value.ToUpperInvariant();
            if (nome.StartsWith("MADRUGADA", StringComparison.Ordinal)) return Madrugada;
            if (nome.StartsWith("MANH", StringComparison.Ordinal)) return Manha;
            if (nome == "TARDE") return Tarde;
            return Noite;
        }

        // Turno da solicitação ("" = não é pedido de turno).
        public static string De(Solicitacao s)
        {
            if (s == null) return "";
            if (s.Turno != null) return s.Turno;
            // Foto gravada por versão anterior, sem o turno (só até a primeira leitura do MV):
            // vale a hora cheia do turno nos pedidos de prescrição e de transferência.
            string origem = s.OrigemEfetiva;
            if (origem != "PRE" && origem != "TRA") return "";
            string hora = (s.Hora ?? "").Trim();
            if (hora.Length > 5) hora = hora.Substring(0, 5);
            switch (hora)
            {
                case "00:01": return Madrugada;
                case "06:01": return Manha;
                case "13:01": return Tarde;
                case "18:01": return Noite;
                default: return "";
            }
        }
    }
}
