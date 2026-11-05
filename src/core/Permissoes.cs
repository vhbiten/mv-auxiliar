using System;

namespace SoulmvKit.Core
{
    // Quem vê o quê, decidido pelo prefixo do login do MV Soul: farmacêutico entra com
    // FA e atendente de farmácia com AF.
    //
    // A regra é por BLOQUEIO, não por liberação: só o prefixo do atendente perde
    // módulo. Login de outro setor, conta de teste ou o usuário "PREVIEW" das flags de
    // screenshot continuam vendo tudo — um prefixo novo não pode nascer sem acesso.
    // A exceção é a Previsão de Consumo, liberada para um único login (ver abaixo).
    //
    // Isto organiza o menu; não é barreira de segurança. O MV continua sendo quem
    // autoriza cada operação no servidor.
    public static class Permissoes
    {
        // Prefixo do login dos atendentes de farmácia (ex.: AF1122).
        private const string PrefixoAtendente = "AF";

        // Único login que vê a Previsão de Consumo.
        private const string LoginPrevisaoConsumo = "FA7324";

        public static bool EhAtendenteFarmacia(string usuario)
        {
            if (string.IsNullOrEmpty(usuario)) return false;
            return usuario.Trim().StartsWith(PrefixoAtendente, StringComparison.OrdinalIgnoreCase);
        }

        // Previsão de Consumo (a Central pede à CAF): só o login liberado. O login é guardado
        // como foi digitado, daí ignorar maiúsculas e espaços. "PREVIEW" não é login: é o
        // usuário das flags de screenshot, sem sessão no MV (--pedido-preview abre esta tela).
        public static bool PodeVerPrevisaoConsumo(string usuario)
        {
            if (string.IsNullOrEmpty(usuario)) return false;
            return usuario == "PREVIEW"
                || string.Equals(usuario.Trim(), LoginPrevisaoConsumo, StringComparison.OrdinalIgnoreCase);
        }

        // Compras CAF (a CAF decide o que comprar): decisão de farmacêutico.
        public static bool PodeVerComprasCaf(string usuario)
        {
            return !EhAtendenteFarmacia(usuario);
        }
    }
}
