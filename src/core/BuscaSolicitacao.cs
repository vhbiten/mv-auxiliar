using System;
using System.Globalization;

namespace SoulmvKit.Core
{
    // Busca livre sobre solicitações (usada pela barra de pesquisa do módulo).
    // Um único campo encontra por nº da solicitação, paciente, atendimento,
    // solicitante ou setor — ignorando maiúsculas/minúsculas e ACENTOS
    // ("jose" acha "JOSÉ"). Com mais de uma palavra, todas precisam bater
    // (em qualquer campo): "maria pediatria" acha a Maria do setor Pediatria.
    public static class BuscaSolicitacao
    {
        public static bool Corresponde(Solicitacao s, string busca)
        {
            if (s == null) return false;
            if (busca == null) return true;
            busca = busca.Trim();
            if (busca.Length == 0) return true;

            string[] termos = busca.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var t in termos)
                if (!TermoBate(s, t)) return false;
            return true;
        }

        private static bool TermoBate(Solicitacao s, string termo)
        {
            return Contem(s.Numero.ToString(), termo)
                || Contem(s.Paciente, termo)
                || Contem(s.Atendimento, termo)
                || Contem(s.Solicitante, termo)
                || Contem(s.Setor, termo);
        }

        // IgnoreNonSpace faz o comparador desprezar os diacríticos (acentos, til,
        // cedilha), então "avore"/"árvore" e "ca"/"ção" casam como se não houvesse acento.
        // MAS º/ª NÃO são diacríticos (são letras à parte) e apóstrofo/hífen/ponto quebram
        // buscas como "santana" x "SANT'ANA" — por isso os dois lados são normalizados antes.
        private static bool Contem(string campo, string termo)
        {
            if (string.IsNullOrEmpty(campo) || string.IsNullOrEmpty(termo)) return false;
            string c = Normalizar(campo), t = Normalizar(termo);
            if (t.Length == 0) return false;
            return CultureInfo.InvariantCulture.CompareInfo.IndexOf(
                c, t, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
        }

        // Remove pontuação de nome (' ’ - .) e traduz ordinais (º/° -> o, ª -> a).
        private static string Normalizar(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\'' || c == '’' || c == '-' || c == '.') continue;
                if (c == 'º' || c == '°') { sb.Append('o'); continue; }   // º e °
                if (c == 'ª') { sb.Append('a'); continue; }                     // ª
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
