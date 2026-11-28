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
            return Bate(s, busca, false, false);
        }

        // Para a lista que mostra o selo "Alta" (comAlta = esta linha está com ele). Ali a palavra
        // "alta", sozinha, quer dizer o selo: digitar "alta" lista de uma vez as solicitações de
        // quem teve alta ("alta uti", as da UTI). Nome com essas letras continua achável com
        // mais uma letra ("altam", "peralta").
        public static bool Corresponde(Solicitacao s, string busca, bool comAlta)
        {
            return Bate(s, busca, true, comAlta);
        }

        private static bool Bate(Solicitacao s, string busca, bool altaEhSelo, bool comAlta)
        {
            if (s == null) return false;
            if (busca == null) return true;
            busca = busca.Trim();
            if (busca.Length == 0) return true;

            string[] termos = busca.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var t in termos)
            {
                if (altaEhSelo && string.Equals(t, "alta", StringComparison.OrdinalIgnoreCase))
                {
                    if (!comAlta) return false;
                    continue;
                }
                if (!TermoBate(s, t)) return false;
            }
            return true;
        }

        // A busca usa a palavra "alta"? (quem mostra o selo refaz o filtro quando chega alta nova)
        public static bool PedeAlta(string busca)
        {
            if (string.IsNullOrEmpty(busca)) return false;
            foreach (var t in busca.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (string.Equals(t, "alta", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Busca livre sobre um TEXTO qualquer (ex.: "código + nome do produto" no
        // pré-pedido): mesmas regras — todos os termos precisam bater, sem acentos.
        public static bool CorrespondeTexto(string campo, string busca)
        {
            if (busca == null) return true;
            busca = busca.Trim();
            if (busca.Length == 0) return true;
            string[] termos = busca.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var t in termos)
                if (!Contem(campo, t)) return false;
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
