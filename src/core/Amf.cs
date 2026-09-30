using System;
using System.Collections.Generic;
using System.Text;

namespace SoulmvKit.Core
{
    // Objeto AMF3 decodificado: a classe (quando tipada) e os campos.
    public class AmfObjeto
    {
        public string Classe;
        public readonly Dictionary<string, object> Campos = new Dictionary<string, object>();

        public object Campo(string nome)
        {
            object v; return Campos.TryGetValue(nome, out v) ? v : null;
        }

        // Procura um campo em profundidade — o DTO do MV vem aninhado.
        public object Buscar(string nome, int profundidade)
        {
            object v = Campo(nome);
            if (v != null) return v;
            if (profundidade <= 0) return null;
            foreach (var par in Campos)
            {
                var o = par.Value as AmfObjeto;
                if (o != null) { object r = o.Buscar(nome, profundidade - 1); if (r != null) return r; }
                var l = par.Value as List<object>;
                if (l != null)
                    foreach (object it in l)
                    {
                        var oi = it as AmfObjeto;
                        if (oi != null) { object r = oi.Buscar(nome, profundidade - 1); if (r != null) return r; }
                    }
            }
            return null;
        }
    }

    // Codec AMF3 (o suficiente para conversar com o BlazeDS do MV).
    //
    // Só existe porque o número que destrava a impressão das justificativas sem assinatura
    // (cdEditorRegistro) não aparece em nenhuma das 214 rotas REST do PEP — ele vem de uma
    // chamada Flex Remoting. Inteiros vão como u29 e strings sempre inline: usar a tabela
    // de referências é opcional para quem codifica, e assim o codificador fica simples.
    public static class Amf
    {
        // ===== ESCRITA =====

        private static void U29(List<byte> b, int v)
        {
            v &= 0x1FFFFFFF;
            if (v < 0x80) b.Add((byte)v);
            else if (v < 0x4000) { b.Add((byte)((v >> 7) | 0x80)); b.Add((byte)(v & 0x7F)); }
            else if (v < 0x200000)
            {
                b.Add((byte)((v >> 14) | 0x80));
                b.Add((byte)(((v >> 7) & 0x7F) | 0x80));
                b.Add((byte)(v & 0x7F));
            }
            else
            {
                b.Add((byte)((v >> 22) | 0x80));
                b.Add((byte)(((v >> 15) & 0x7F) | 0x80));
                b.Add((byte)(((v >> 8) & 0x7F) | 0x80));
                b.Add((byte)(v & 0xFF));
            }
        }

        // UTF8-VR: sem marcador. String vazia é sempre 0x01.
        private static void Texto(List<byte> b, string s)
        {
            if (string.IsNullOrEmpty(s)) { b.Add(0x01); return; }
            byte[] u = Encoding.UTF8.GetBytes(s);
            U29(b, (u.Length << 1) | 1);
            b.AddRange(u);
        }

        private static void ValorTexto(List<byte> b, string s) { b.Add(0x06); Texto(b, s); }
        private static void ValorInteiro(List<byte> b, int v) { b.Add(0x04); U29(b, v); }
        private static void ValorNulo(List<byte> b) { b.Add(0x01); }

        // Monta o pacote AMF0 inteiro com uma RemotingMessage AMF3 dentro.
        public static byte[] MontarChamada(string operacao, string source, string destino,
                                           string dsId, string endpoint, object[] args, int sequencia)
        {
            var m = new List<byte>();

            // RemotingMessage: 9 membros selados, na ordem que o BlazeDS espera.
            m.Add(0x0A);
            U29(m, (9 << 4) | 3);                 // traços inline, não dinâmico, 9 membros
            Texto(m, "flex.messaging.messages.RemotingMessage");
            foreach (string nome in new string[] { "operation", "source", "clientId", "messageId",
                                                   "timeToLive", "destination", "headers", "body", "timestamp" })
                Texto(m, nome);

            ValorTexto(m, operacao);
            if (source == null) ValorNulo(m); else ValorTexto(m, source);
            ValorNulo(m);                          // clientId
            ValorTexto(m, Guid.NewGuid().ToString().ToUpperInvariant());
            ValorInteiro(m, 0);                    // timeToLive
            ValorTexto(m, destino);

            // headers: objeto dinâmico anônimo
            m.Add(0x0A);
            U29(m, 11);                            // traços inline, dinâmico, 0 membros selados
            Texto(m, "");                          // classe anônima
            Texto(m, "DSId"); ValorTexto(m, string.IsNullOrEmpty(dsId) ? "nil" : dsId);
            Texto(m, "DSEndpoint"); if (endpoint == null) ValorNulo(m); else ValorTexto(m, endpoint);
            Texto(m, "");                          // fim dos dinâmicos

            // body: array dos argumentos
            m.Add(0x09);
            U29(m, (args.Length << 1) | 1);
            Texto(m, "");                          // sem parte associativa
            foreach (object a in args)
            {
                if (a == null) ValorNulo(m);
                else if (a is int) ValorInteiro(m, (int)a);
                else if (a is string) ValorTexto(m, (string)a);
                else if (a is bool) m.Add((byte)((bool)a ? 0x03 : 0x02));
                else ValorTexto(m, a.ToString());
            }

            ValorInteiro(m, 0);                    // timestamp

            // Envelope AMF0
            var p = new List<byte>();
            p.Add(0x00); p.Add(0x03);              // versão 3
            p.Add(0x00); p.Add(0x00);              // 0 headers
            p.Add(0x00); p.Add(0x01);              // 1 body
            byte[] alvo = Encoding.UTF8.GetBytes("null");
            p.Add((byte)(alvo.Length >> 8)); p.Add((byte)alvo.Length); p.AddRange(alvo);
            byte[] resp = Encoding.UTF8.GetBytes("/" + sequencia);
            p.Add((byte)(resp.Length >> 8)); p.Add((byte)resp.Length); p.AddRange(resp);

            var corpo = new List<byte>();
            corpo.Add(0x0A);                                        // AMF0 strict array
            corpo.Add(0); corpo.Add(0); corpo.Add(0); corpo.Add(1); // 1 elemento
            corpo.Add(0x11);                                        // troca para AMF3
            corpo.AddRange(m);

            p.Add((byte)(corpo.Count >> 24)); p.Add((byte)(corpo.Count >> 16));
            p.Add((byte)(corpo.Count >> 8)); p.Add((byte)corpo.Count);
            p.AddRange(corpo);
            return p.ToArray();
        }

        // ===== LEITURA =====

        private class Leitor
        {
            public byte[] B; public int P;
            public readonly List<string> Textos = new List<string>();
            public readonly List<object> Objetos = new List<object>();
            public readonly List<string[]> Tracos = new List<string[]>();
            public readonly List<string> Classes = new List<string>();

            public byte Byte() { return B[P++]; }
            public int U29()
            {
                int v = 0;
                for (int i = 0; i < 3; i++)
                {
                    byte c = B[P++];
                    v = (v << 7) | (c & 0x7F);
                    if ((c & 0x80) == 0) return v;
                }
                return (v << 8) | B[P++];
            }
            public double Duplo()
            {
                var t = new byte[8];
                for (int i = 0; i < 8; i++) t[7 - i] = B[P + i];
                P += 8;
                return BitConverter.ToDouble(t, 0);
            }
        }

        private static string LerTexto(Leitor l)
        {
            int h = l.U29();
            if ((h & 1) == 0) return l.Textos[h >> 1];
            int n = h >> 1;
            if (n == 0) return "";
            string s = Encoding.UTF8.GetString(l.B, l.P, n);
            l.P += n;
            l.Textos.Add(s);
            return s;
        }

        private static object LerValor(Leitor l)
        {
            byte marca = l.Byte();
            switch (marca)
            {
                case 0x00: case 0x01: return null;      // undefined / null
                case 0x02: return false;
                case 0x03: return true;
                case 0x04: { int v = l.U29(); if ((v & 0x10000000) != 0) v -= 0x20000000; return v; }
                case 0x05: return l.Duplo();
                case 0x06: return LerTexto(l);
                case 0x08:                               // date
                    {
                        int h = l.U29();
                        if ((h & 1) == 0) return l.Objetos[h >> 1];
                        double ms = l.Duplo();
                        object d = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms).ToLocalTime();
                        l.Objetos.Add(d);
                        return d;
                    }
                case 0x09: return LerArray(l);
                case 0x0A: return LerObjeto(l);
                case 0x0C:                               // byte array
                    {
                        int h = l.U29();
                        if ((h & 1) == 0) return l.Objetos[h >> 1];
                        int n = h >> 1;
                        var t = new byte[n];
                        Array.Copy(l.B, l.P, t, 0, n);
                        l.P += n;
                        l.Objetos.Add(t);
                        return t;
                    }
                default:
                    throw new Exception("AMF3: marcador " + marca + " não suportado (posição " + (l.P - 1) + ")");
            }
        }

        private static object LerArray(Leitor l)
        {
            int h = l.U29();
            if ((h & 1) == 0) return l.Objetos[h >> 1];
            int n = h >> 1;
            var lista = new List<object>();
            l.Objetos.Add(lista);
            while (true)                                  // parte associativa
            {
                string chave = LerTexto(l);
                if (chave.Length == 0) break;
                LerValor(l);
            }
            for (int i = 0; i < n; i++) lista.Add(LerValor(l));
            return lista;
        }

        private static object LerObjeto(Leitor l)
        {
            int h = l.U29();
            if ((h & 1) == 0) return l.Objetos[h >> 1];

            string[] membros; string classe;
            if (((h >> 1) & 1) == 0)                      // traços por referência
            {
                int idx = h >> 2;
                membros = l.Tracos[idx];
                classe = l.Classes[idx];
                var o = new AmfObjeto { Classe = classe };
                l.Objetos.Add(o);
                foreach (string nome in membros) o.Campos[nome] = LerValor(l);
                return o;
            }

            bool externo = ((h >> 2) & 1) != 0;
            bool dinamico = ((h >> 3) & 1) != 0;
            int qtd = h >> 4;
            classe = LerTexto(l);
            membros = new string[qtd];
            for (int i = 0; i < qtd; i++) membros[i] = LerTexto(l);
            l.Tracos.Add(membros); l.Classes.Add(classe);

            var obj = new AmfObjeto { Classe = classe };
            l.Objetos.Add(obj);
            if (externo)
            {
                obj.Campos["__externo"] = LerValor(l);
                return obj;
            }
            foreach (string nome in membros) obj.Campos[nome] = LerValor(l);
            if (dinamico)
                while (true)
                {
                    string chave = LerTexto(l);
                    if (chave.Length == 0) break;
                    obj.Campos[chave] = LerValor(l);
                }
            return obj;
        }

        // Lê o pacote de resposta e devolve o corpo do primeiro body, com o alvo ("/1/onResult").
        public static object LerResposta(byte[] pacote, out string alvo)
        {
            alvo = null;
            int p = 2;                                    // versão
            int nHeaders = (pacote[p] << 8) | pacote[p + 1]; p += 2;
            for (int i = 0; i < nHeaders; i++)
            {
                int n = (pacote[p] << 8) | pacote[p + 1]; p += 2 + n;
                p += 1;                                   // mustUnderstand
                int tam = (pacote[p] << 24) | (pacote[p + 1] << 16) | (pacote[p + 2] << 8) | pacote[p + 3];
                p += 4 + tam;
            }
            int nBodies = (pacote[p] << 8) | pacote[p + 1]; p += 2;
            if (nBodies < 1) return null;

            int na = (pacote[p] << 8) | pacote[p + 1]; p += 2;
            alvo = Encoding.UTF8.GetString(pacote, p, na); p += na;
            int nr = (pacote[p] << 8) | pacote[p + 1]; p += 2 + nr;
            p += 4;                                       // tamanho do corpo

            if (pacote[p] == 0x11) p++;                   // marcador avmplus
            var l = new Leitor { B = pacote, P = p };
            return LerValor(l);
        }
    }
}
