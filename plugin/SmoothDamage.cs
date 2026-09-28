// Space Travel Idle community mod - smooth damage mitigation
//
// Replaces the game's damage rule (BattleUnit.ReceiveAttack):
//   vanilla: damage = attack - defense   (binary: zero or one-shot)
//   modded:  damage = attack * attack / (attack + defense)
// Defense always reduces damage by a percentage and never to zero:
// def == atk -> half damage, def == 9x atk -> 10% damage, etc.
// Applies to both the player and enemies.
//
// Compiled with the .NET Framework 4 csc against the game's own
// Assembly-CSharp.dll and the BepInEx 5 core DLLs (see build_plugin.ps1).

using System.Reflection;
using BepInEx;
using HarmonyLib;
using LargeNumbers;

[BepInPlugin("sti.community.smoothdamage", "STI Community Smooth Damage", "1.0.0")]
public class SmoothDamagePlugin : BaseUnityPlugin
{
    private void Awake()
    {
        Harmony harmony = new Harmony("sti.community.smoothdamage");
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        Logger.LogInfo("Smooth damage active: dmg = atk*atk/(atk+def)");
    }
}

[HarmonyPatch(typeof(BattleUnit), "ReceiveAttack")]
public static class ReceiveAttackPatch
{
    private static readonly MethodInfo LoseHPMethod =
        AccessTools.Method(typeof(BattleUnit), "LoseHP");

    private static readonly MethodInfo RollBuffMethod =
        AccessTools.Method(typeof(BattleUnit), "RollBuffOfType");

    private static void RollCharge(BattleUnit unit)
    {
        RollBuffMethod.Invoke(unit, new object[] { BuffType.charge });
    }

    // Full replacement of ReceiveAttack: identical hit/dodge roll and charge
    // handling, only the mitigation formula differs.
    public static bool Prefix(BattleUnit __instance, BattleUnit caster,
                              CardEffect effect, ref ScientificNotation __result)
    {
        double hit = effect.successChance * caster.hitChance;
        double missChance = 1.0 - (hit - __instance.dodgeChance);
        if (Utils.Rand01() < missChance)
        {
            RollCharge(caster);
            __result = ScientificNotation.minusOne;
            return false;
        }

        ScientificNotation amount = effect.GetActualDealAmount(caster, __instance);
        ScientificNotation damage;
        if (amount <= ScientificNotation.zero)
        {
            damage = ScientificNotation.zero;
        }
        else
        {
            ScientificNotation def = __instance.def;
            if (def < ScientificNotation.zero)
            {
                def = ScientificNotation.zero;
            }
            damage = amount * amount / (amount + def);
        }

        __result = (ScientificNotation)LoseHPMethod.Invoke(__instance, new object[] { damage });
        RollCharge(caster);
        return false;
    }
}
