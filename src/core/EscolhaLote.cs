using System;
using System.Collections.Generic;
using System.Linq;

namespace SoulmvKit.Core
{
    // Regra de escolha de lote (definida pela farmácia):
    //   escolher o lote com a MAIOR quantidade em estoque (maior saldo).
    //   Empate de saldo -> validade mais próxima.
    // A quantidade necessária só é usada para avisar quando nem o maior lote cobre.
    public static class EscolhaLote
    {
        public static ResultadoLote Escolher(IEnumerable<LoteOption> lotes, decimal qtdNecessaria)
        {
            var res = new ResultadoLote();
            var candidatos = (lotes ?? new List<LoteOption>())
                .Where(l => l != null && l.SaldoAtual > 0)
                .ToList();

            if (candidatos.Count == 0)
            {
                res.SaldoSuficiente = false;
                res.Aviso = "Nenhum lote com saldo disponível.";
                return res;
            }

            // Lotes sem validade ficam por último no desempate (validade distante)
            Func<LoteOption, DateTime> chaveValidade =
                l => l.Validade.HasValue ? l.Validade.Value : DateTime.MaxValue;

            var escolhido = candidatos
                .OrderByDescending(l => l.SaldoAtual)   // 1º: maior quantidade
                .ThenBy(chaveValidade)                  // desempate: validade mais próxima
                .First();

            res.Lote = escolhido;
            res.SaldoSuficiente = escolhido.SaldoAtual >= qtdNecessaria;
            if (!res.SaldoSuficiente)
            {
                res.Aviso = "O lote de maior saldo (" + escolhido.SaldoAtual +
                            ") não cobre a quantidade necessária (" + qtdNecessaria + ").";
            }
            return res;
        }
    }
}
