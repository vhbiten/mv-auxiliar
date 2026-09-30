using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SoulmvKit.Core
{
    // Cruza "quem está em uso de antimicrobiano" com "quais justificativas foram emitidas",
    // para apontar o médico que prescreveu e não justificou.
    //
    // Duas coisas que o MV NÃO entrega e que moldam a regra:
    //
    //  1. A justificativa não diz a qual antibiótico se refere — o conteúdo do documento
    //     mora no editor Flash. Então "o paciente tem justificativa" não é o mesmo que
    //     "este antibiótico está justificado". O que dá para afirmar com segurança é a
    //     DATA: se a justificativa é anterior ao início deste tratamento, ela foi escrita
    //     para outra coisa. Daí o critério ser por data de início, e não por presença.
    //
    //  2. Nem todo antimicrobiano exige justificativa (o MV marca item a item, mas só no
    //     payload Flash). Por isso o padrão aqui é EXIGIR de todos, e a farmácia dispensa
    //     quem o protocolo dispensar em dados/dispensa-justificativa.txt. Deixar de avisar
    //     um caso real é pior que um aviso a mais.
    public static class CruzamentoJustificativas
    {
        // Um dia de folga: acontece de o médico justificar na véspera de começar.
        private const int ToleranciaDias = 1;

        // Cola a justificativa em cada antimicrobiano. Devolve quantos ficaram descobertos.
        public static int Aplicar(List<ItemAntimicrobiano> itens, List<Justificativa> justificativas)
        {
            if (itens == null || itens.Count == 0) return 0;

            // Indexado pelas DUAS chaves: o atendimento é o normal, mas um paciente
            // transferido troca de atendimento e a justificativa antiga ficaria órfã.
            var porAtendimento = new Dictionary<int, List<Justificativa>>();
            var porPaciente = new Dictionary<int, List<Justificativa>>();
            if (justificativas != null)
                foreach (var j in justificativas)
                {
                    if (j.Atendimento > 0) Somar(porAtendimento, j.Atendimento, j);
                    if (j.PacienteId > 0) Somar(porPaciente, j.PacienteId, j);
                }

            int descobertos = 0;
            foreach (var it in itens)
            {
                it.ExigeJustificativa = DispensaJustificativa.Exige(it.Produto);
                it.JustificadoEm = DateTime.MinValue;

                DateTime piso = it.InicioTratamento == DateTime.MinValue
                    ? DateTime.MinValue                                   // sem "D n/N": aceita qualquer uma
                    : it.InicioTratamento.AddDays(-ToleranciaDias);

                Melhor(porAtendimento, it.Atendimento, piso, it);
                Melhor(porPaciente, it.PacienteId, piso, it);

                if (it.ExigeJustificativa && !it.Justificado) descobertos++;
            }
            return descobertos;
        }

        // Guarda a justificativa MAIS RECENTE que ainda cobre este tratamento.
        private static void Melhor(Dictionary<int, List<Justificativa>> mapa, int chave, DateTime piso, ItemAntimicrobiano it)
        {
            List<Justificativa> lista;
            if (chave <= 0 || !mapa.TryGetValue(chave, out lista)) return;
            foreach (var j in lista)
            {
                if (j.Data == DateTime.MinValue) continue;
                if (piso != DateTime.MinValue && j.Data.Date < piso.Date) continue;
                if (j.Data > it.JustificadoEm) it.JustificadoEm = j.Data;
            }
        }

        private static void Somar(Dictionary<int, List<Justificativa>> mapa, int chave, Justificativa j)
        {
            List<Justificativa> l;
            if (!mapa.TryGetValue(chave, out l)) { l = new List<Justificativa>(); mapa[chave] = l; }
            l.Add(j);
        }

        // Quantos dias de justificativa é preciso buscar para cobrir os tratamentos em curso.
        // Quem está em D13 começou há 12 dias; a justificativa dele é de lá.
        public static int JanelaNecessaria(List<ItemAntimicrobiano> itens, int minimo)
        {
            int maior = 0;
            if (itens != null) foreach (var i in itens) if (i.DiaAtual > maior) maior = i.DiaAtual;
            int dias = maior + ToleranciaDias + 2;
            return dias < minimo ? minimo : dias;
        }
    }

    // Antimicrobianos que o protocolo do hospital DISPENSA de justificativa.
    //
    // Por omissão todo antimicrobiano exige — é o lado seguro: um aviso a mais custa uma
    // conferência, um aviso a menos deixa passar. A farmácia tira daqui o que não precisa
    // (nitrofurantoína de profilaxia urinária é o caso típico).
    public static class DispensaJustificativa
    {
        private static string _caminho;
        private static readonly List<string> _dispensados = new List<string>();

        public static void Init(string baseDir)
        {
            string dir = Path.Combine(baseDir, "dados");
            Directory.CreateDirectory(dir);
            _caminho = Path.Combine(dir, "dispensa-justificativa.txt");
            _dispensados.Clear();
            try
            {
                if (!File.Exists(_caminho)) { CriarModelo(); return; }
                foreach (var linha in File.ReadAllLines(_caminho))
                {
                    string s = (linha ?? "").Trim();
                    if (s.Length == 0 || s.StartsWith("#")) continue;
                    int cerq = s.IndexOf('#');
                    if (cerq >= 0) s = s.Substring(0, cerq).Trim();
                    s = Normalizar(s);
                    if (s.Length >= 4 && !_dispensados.Contains(s)) _dispensados.Add(s);
                }
                Logger.Log("Justificativa dispensada para " + _dispensados.Count + " itens (arquivo " + _caminho + ").");
            }
            catch (Exception ex) { Logger.Log("Dispensa de justificativa: falha ao ler " + _caminho + ": " + ex.Message); }
        }

        private static void CriarModelo()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Antimicrobianos que o protocolo do hospital NAO exige justificativa.");
                sb.AppendLine("#");
                sb.AppendLine("# Por padrao TODO antimicrobiano prescrito exige justificativa, e a aba");
                sb.AppendLine("# Antimicrobianos marca com X quem esta sem. Este arquivo e a lista de excecoes:");
                sb.AppendLine("# o que estiver aqui deixa de ser cobrado e aparece com um traco na coluna.");
                sb.AppendLine("#");
                sb.AppendLine("# Basta o INICIO do nome do produto, como ele aparece na coluna \"Antimicrobiano\":");
                sb.AppendLine("#");
                sb.AppendLine("#   NITROFURANTOINA");
                sb.AppendLine("#   AZITROMICINA 500MG CP");
                sb.AppendLine("#");
                sb.AppendLine("# Confirme a lista com a CCIH antes de dispensar qualquer um.");
                sb.AppendLine("# Depois de editar, feche e abra o programa.");
                sb.AppendLine();
                File.WriteAllText(_caminho, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Logger.Log("Dispensa de justificativa: não consegui criar " + _caminho + ": " + ex.Message); }
        }

        // true = este produto precisa de justificativa.
        public static bool Exige(string produto)
        {
            if (_dispensados.Count == 0) return true;
            string n = Normalizar(produto);
            if (n.Length == 0) return true;
            foreach (string d in _dispensados) if (n.StartsWith(d, StringComparison.Ordinal)) return false;
            return true;
        }

        private static string Normalizar(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string d = s.Trim().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(d.Length);
            foreach (char c in d)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
            return Regex.Replace(sb.ToString().ToUpperInvariant(), @"\s+", " ").Trim();
        }
    }
}
