using System;
using System.Collections.Generic;
using System.IO;

namespace SoulmvKit.Core
{
    // Preferências do usuário desta máquina (tela Configurações), em dados\preferencias.txt,
    // uma "chave=valor" por linha. Diferente do config.txt (servidor/estação, da MÁQUINA):
    // aqui só escolhas de uso, que a atualização automática nunca sobrescreve (dados\).
    public static class Preferencias
    {
        private static readonly object _lock = new object();
        private static Dictionary<string, string> _valores;

        private static string Caminho()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dados", "preferencias.txt");
        }

        private static Dictionary<string, string> Carregar()
        {
            if (_valores != null) return _valores;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(Caminho()))
                    foreach (string l in File.ReadAllLines(Caminho()))
                    {
                        int i = l.IndexOf('=');
                        if (i > 0) d[l.Substring(0, i).Trim()] = l.Substring(i + 1).Trim();
                    }
            }
            catch (Exception ex) { Logger.Log("Preferências: falha ao ler: " + ex.Message); }
            return _valores = d;
        }

        public static bool Bool(string chave, bool padrao)
        {
            lock (_lock)
            {
                string v;
                if (!Carregar().TryGetValue(chave, out v)) return padrao;
                return v == "1" || string.Equals(v, "sim", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
            }
        }

        public static void Definir(string chave, bool valor)
        {
            lock (_lock)
            {
                Carregar()[chave] = valor ? "1" : "0";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Caminho()));
                    var linhas = new List<string>();
                    foreach (var kv in _valores) linhas.Add(kv.Key + "=" + kv.Value);
                    File.WriteAllLines(Caminho(), linhas.ToArray());
                }
                catch (Exception ex) { Logger.Log("Preferências: falha ao gravar: " + ex.Message); }
            }
        }
    }
}
