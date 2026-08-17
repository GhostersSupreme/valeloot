/**
 * Player-facing labels and search aliases keyed by the game's canonical stat IDs.
 *
 * Filter rules keep the canonical key; this vocabulary is presentation-only so labels can improve
 * without migrating saved filters.
 */
export const STAT_VOCABULARY = {
  "Str": {
    "label": "Str",
    "aliases": []
  },
  "Vit": {
    "label": "Vit",
    "aliases": []
  },
  "Agi": {
    "label": "Agi",
    "aliases": []
  },
  "Dex": {
    "label": "Dex",
    "aliases": []
  },
  "Int": {
    "label": "Int",
    "aliases": []
  },
  "Luk": {
    "label": "Luk",
    "aliases": []
  },
  "AllStats": {
    "label": "All Stats",
    "aliases": []
  },
  "Hp": {
    "label": "Hp",
    "aliases": [
      "health",
      "hitpoints",
      "hit points"
    ]
  },
  "Mp": {
    "label": "Mp",
    "aliases": [
      "mana"
    ]
  },
  "Atk": {
    "label": "Atk",
    "aliases": [
      "attack"
    ]
  },
  "Matk": {
    "label": "Matk",
    "aliases": [
      "magic attack"
    ]
  },
  "Def": {
    "label": "Def",
    "aliases": [
      "defense",
      "defence"
    ]
  },
  "Mdef": {
    "label": "Mdef",
    "aliases": [
      "magic defense",
      "magic defence"
    ]
  },
  "DefFlat": {
    "label": "Flat Def",
    "aliases": [
      "flat defense",
      "flat defence"
    ]
  },
  "MdefFlat": {
    "label": "Flat Mdef",
    "aliases": [
      "flat magic defense",
      "flat magic defence"
    ]
  },
  "Hit": {
    "label": "Hit",
    "aliases": [
      "accuracy"
    ]
  },
  "Flee": {
    "label": "Flee",
    "aliases": [
      "dodge",
      "evasion"
    ]
  },
  "Crit": {
    "label": "Crit",
    "aliases": [
      "critical",
      "crit chance"
    ]
  },
  "CritDamage": {
    "label": "Crit Damage",
    "aliases": [
      "critical damage"
    ]
  },
  "CritDef": {
    "label": "Crit Def",
    "aliases": [
      "critical defense",
      "critical defence"
    ]
  },
  "Block": {
    "label": "Block",
    "aliases": []
  },
  "AtkSpd": {
    "label": "Attack Speed",
    "aliases": [
      "attack speed",
      "aspd"
    ]
  },
  "CastSpd": {
    "label": "Cast Speed",
    "aliases": [
      "casting speed",
      "cspd"
    ]
  },
  "MoveSpd": {
    "label": "Movement Speed",
    "aliases": [
      "movement speed"
    ]
  },
  "AtkSpdLimit": {
    "label": "Attack Speed Limit",
    "aliases": [
      "attack speed cap",
      "attack speed limit",
      "aspd cap",
      "aspd limit"
    ]
  },
  "AtkMult": {
    "label": "Atk",
    "aliases": [
      "attack"
    ]
  },
  "MatkMult": {
    "label": "Matk",
    "aliases": [
      "magic attack"
    ]
  },
  "DefMult": {
    "label": "Def",
    "aliases": [
      "defense",
      "defence"
    ]
  },
  "MdefMult": {
    "label": "Mdef",
    "aliases": [
      "magic defense",
      "magic defence"
    ]
  },
  "HpMult": {
    "label": "Hp",
    "aliases": [
      "health"
    ]
  },
  "MpMult": {
    "label": "Mp",
    "aliases": [
      "mana"
    ]
  },
  "FleeMult": {
    "label": "Total Flee",
    "aliases": []
  },
  "HitMult": {
    "label": "Total Hit",
    "aliases": []
  },
  "CritMult": {
    "label": "Total Crit Rate",
    "aliases": [
      "critical",
      "crit chance"
    ]
  },
  "HpRegen": {
    "label": "Hp Regen",
    "aliases": [
      "health regen",
      "health regeneration"
    ]
  },
  "MpRegen": {
    "label": "Mp Regen",
    "aliases": [
      "mana regen",
      "mana regeneration"
    ]
  },
  "HpRegenMult": {
    "label": "Hp Regen",
    "aliases": [
      "health regen"
    ]
  },
  "MpRegenMult": {
    "label": "Mp Regen",
    "aliases": [
      "mana regen"
    ]
  },
  "HpRegenMax": {
    "label": "Max Hp Regen",
    "aliases": []
  },
  "MpRegenMax": {
    "label": "Max Mp Regen",
    "aliases": []
  },
  "Healing": {
    "label": "Healing",
    "aliases": []
  },
  "HealingReceived": {
    "label": "Healing Received",
    "aliases": []
  },
  "HealingToBarrier": {
    "label": "Healing to Barrier",
    "aliases": []
  },
  "Leech": {
    "label": "Health Leech",
    "aliases": [
      "lifesteal",
      "life steal"
    ]
  },
  "LeechKill": {
    "label": "Health on Kill",
    "aliases": []
  },
  "LeechKillMp": {
    "label": "Mana on Kill",
    "aliases": []
  },
  "SiphonHp": {
    "label": "Siphon Hp",
    "aliases": [
      "hp siphon"
    ]
  },
  "SiphonMp": {
    "label": "Siphon Mp",
    "aliases": [
      "mp siphon"
    ]
  },
  "Range": {
    "label": "Range",
    "aliases": [
      "attack range"
    ]
  },
  "CastRange": {
    "label": "Cast Range",
    "aliases": []
  },
  "WeightLimit": {
    "label": "Weight Limit",
    "aliases": [
      "carry weight"
    ]
  },
  "DoubleAttack": {
    "label": "Multistrike",
    "aliases": [
      "double attack"
    ]
  },
  "Chain": {
    "label": "Auto-Attack Chain",
    "aliases": []
  },
  "Splash": {
    "label": "Auto-Attack Splash",
    "aliases": []
  },
  "MpCost": {
    "label": "Mp Cost",
    "aliases": [
      "mana cost"
    ]
  },
  "ReflectDamage": {
    "label": "Reflect Damage",
    "aliases": [
      "damage reflect",
      "thorns"
    ]
  },
  "ReflectSpell": {
    "label": "Spell Reflect",
    "aliases": []
  },
  "ThreatMult": {
    "label": "Threat Generation",
    "aliases": []
  },
  "ExpRate": {
    "label": "EXP Rate",
    "aliases": []
  },
  "DropRate": {
    "label": "Drop Rate",
    "aliases": []
  },
  "DefPierce": {
    "label": "Def Pierce",
    "aliases": [
      "defense pierce",
      "armor penetration",
      "armour penetration"
    ]
  },
  "MdefPierce": {
    "label": "Mdef Pierce",
    "aliases": [
      "magic defense pierce"
    ]
  },
  "MatkPerStr": {
    "label": "Matk per Str",
    "aliases": [
      "magic attack per str"
    ]
  },
  "AtkPerStr": {
    "label": "Atk per Str",
    "aliases": [
      "attack per str"
    ]
  },
  "AllResist": {
    "label": "All Resist (except Neutral)",
    "aliases": []
  },
  "PerfectDodge": {
    "label": "Perfect Dodge",
    "aliases": []
  },
  "PerfectHit": {
    "label": "Perfect Hit",
    "aliases": []
  },
  "PerfectCloak": {
    "label": "Perfect Cloak",
    "aliases": []
  },
  "DamageMelee": {
    "label": "Melee Damage",
    "aliases": []
  },
  "DamageMagic": {
    "label": "Magic Damage",
    "aliases": []
  },
  "DamageRanged": {
    "label": "Ranged Damage",
    "aliases": []
  },
  "DamageStatus": {
    "label": "Status Damage",
    "aliases": []
  },
  "DamageCloseRange": {
    "label": "Close-Range Damage",
    "aliases": []
  },
  "DamageFarRange": {
    "label": "Far-Range Damage",
    "aliases": []
  },
  "DamageFromMagic": {
    "label": "Damage from Magic",
    "aliases": []
  },
  "DamageFromMelee": {
    "label": "Damage from Melee",
    "aliases": []
  },
  "DamageFromRanged": {
    "label": "Damage from Ranged",
    "aliases": []
  },
  "FinalDamageReduction": {
    "label": "Final Damage Reduction",
    "aliases": [
      "damage reduction"
    ]
  },
  "BuffDuration": {
    "label": "Buff Duration",
    "aliases": []
  },
  "StatusDuration": {
    "label": "Status Duration",
    "aliases": []
  },
  "CooldownRecovery": {
    "label": "Cooldown Recovery",
    "aliases": []
  },
  "StatusMaxStacks": {
    "label": "Max Stacks",
    "aliases": []
  },
  "AutoattackMatk": {
    "label": "Auto-attack uses Matk",
    "aliases": [
      "auto attack magic",
      "magic auto attack"
    ]
  },
  "AfterCastDelay": {
    "label": "After Cast Delay",
    "aliases": []
  },
  "NoCastCancel": {
    "label": "Uninterruptible Cast",
    "aliases": []
  },
  "NoKnockback": {
    "label": "Knockback Immunity",
    "aliases": []
  },
  "NoFlinch": {
    "label": "Flinch Immunity",
    "aliases": []
  },
  "NoReflect": {
    "label": "Reflected Damage Immunity",
    "aliases": []
  },
  "NoAttack": {
    "label": "Cannot Attack",
    "aliases": []
  },
  "SummonAllStats": {
    "label": "Summon All Stats",
    "aliases": []
  },
  "SummonHpMult": {
    "label": "Summon Hp",
    "aliases": []
  },
  "SummonAtkSpd": {
    "label": "Summon Attack Speed",
    "aliases": [
      "summon attack speed"
    ]
  },
  "SummonAtkMult": {
    "label": "Summon Atk",
    "aliases": []
  },
  "SummonMatkMult": {
    "label": "Summon Matk",
    "aliases": []
  },
  "SummonHealing": {
    "label": "Summon Healing",
    "aliases": []
  },
  "SummonResist": {
    "label": "Summon Resist",
    "aliases": []
  }
} as const;
