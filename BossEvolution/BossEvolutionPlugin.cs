using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Xna.Framework;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;

namespace BossEvolution;

[ApiVersion(2, 1)]
public class BossEvolutionPlugin : TerrariaPlugin
{
    public override string Name => "BossEvolution";
    public override string Author => "Custom";
    public override string Description => "Boss phases, random skills, nicknames for TShock 6";
    public override Version Version => new(1, 0, 0);

    private static string ConfigPath => Path.Combine(TShock.SavePath, "BossEvolution.json");
    private Config _config = new();
    private readonly Dictionary<int, BossState> _bosses = new();
    private readonly Random _rng = new();

    private static readonly string[] NickPrefixes =
    {
        "Furious", "Ancient", "Cursed", "Eternal", "Chaotic",
        "Shadow", "Bloody", "Divine", "Corrupted", "Prime"
    };

    private static readonly string[] NickSuffixes =
    {
        "the Destroyer", "of Nightmares", "the Immortal", "World Ender",
        "Soul Reaper", "the Awakened", "of the Abyss", "the Relentless"
    };

    public BossEvolutionPlugin(Main game) : base(game)
    {
    }

    public override void Initialize()
    {
        LoadConfig();

        ServerApi.Hooks.NpcSpawn.Register(this, OnNpcSpawn);
        ServerApi.Hooks.NpcKilled.Register(this, OnNpcKilled);
        ServerApi.Hooks.NpcAIUpdate.Register(this, OnNpcAI);
        GeneralHooks.ReloadEvent += OnReload;

        Commands.ChatCommands.Add(new Command("bossevolution.admin", CmdBossEvo, "bossevo")
        {
            HelpText = "/bossevo reload | /bossevo info"
        });

        TShock.Log.ConsoleInfo("[BossEvolution] Loaded.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ServerApi.Hooks.NpcSpawn.Deregister(this, OnNpcSpawn);
            ServerApi.Hooks.NpcKilled.Deregister(this, OnNpcKilled);
            ServerApi.Hooks.NpcAIUpdate.Deregister(this, OnNpcAI);
            GeneralHooks.ReloadEvent -= OnReload;
        }

        base.Dispose(disposing);
    }

    private void OnReload(ReloadEventArgs args)
    {
        LoadConfig();
        args.Player.SendSuccessMessage("[BossEvolution] Config reloaded.");
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                _config = Config.Default();
                var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(ConfigPath, json);
            }
            else
            {
                var json = File.ReadAllText(ConfigPath);
                _config = JsonSerializer.Deserialize<Config>(json) ?? Config.Default();
            }
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError("[BossEvolution] Config error: " + ex.Message);
            _config = Config.Default();
        }
    }

    private void OnNpcSpawn(NpcSpawnEventArgs args)
    {
        if (args.Handled)
            return;

        var npc = Main.npc[args.NpcId];
        if (npc == null || !npc.active || !npc.boss)
            return;

        if (!_config.Enabled)
            return;

        if (_config.EnabledBossIds.Count > 0 && !_config.EnabledBossIds.Contains(npc.type))
            return;

        var state = new BossState
        {
            NpcIndex = args.NpcId,
            NpcType = npc.type,
            Phase = 1,
            Nickname = GenerateNickname(npc),
            MaxLife = npc.lifeMax,
            Skills = RollSkills(1)
        };

        if (_config.HpMultiplier > 1f)
        {
            npc.lifeMax = (int)(npc.lifeMax * _config.HpMultiplier);
            npc.life = npc.lifeMax;
            state.MaxLife = npc.lifeMax;
        }

        _bosses[args.NpcId] = state;

        TSPlayer.All.SendMessage(
            "[BossEvolution] " + state.Nickname + " telah muncul! (Fase 1)",
            Color.OrangeRed);
    }

    private void OnNpcKilled(NpcKilledEventArgs args)
    {
        if (args.npc == null)
            return;

        if (_bosses.TryGetValue(args.npc.whoAmI, out var state))
        {
            TSPlayer.All.SendMessage(
                "[BossEvolution] " + state.Nickname + " telah dikalahkan!",
                Color.LimeGreen);
            _bosses.Remove(args.npc.whoAmI);
        }
    }

    private void OnNpcAI(NpcAiUpdateEventArgs args)
    {
        var npc = args.Npc;
        if (npc == null || !npc.active || !npc.boss)
            return;

        if (!_bosses.TryGetValue(npc.whoAmI, out var state))
            return;

        float ratio = state.MaxLife <= 0 ? 1f : (float)npc.life / state.MaxLife;

        if (state.Phase == 1 && ratio <= _config.Phase2HpRatio)
            EnterPhase(npc, state, 2);
        else if (state.Phase == 2 && ratio <= _config.Phase3HpRatio)
            EnterPhase(npc, state, 3);

        ApplySkills(npc, state);
    }

    private void EnterPhase(NPC npc, BossState state, int phase)
    {
        state.Phase = phase;
        state.Skills = RollSkills(phase);
        state.SkillCooldown = 0;

        string skillNames = string.Join(", ", state.Skills);
        TSPlayer.All.SendMessage(
            "[BossEvolution] " + state.Nickname + " masuk FASE " + phase + "! Skill: " + skillNames,
            Color.MediumPurple);

        if (_config.PhaseHealPercent > 0)
        {
            int heal = (int)(state.MaxLife * _config.PhaseHealPercent);
            npc.life = Math.Min(npc.life + heal, npc.lifeMax);
            npc.HealEffect(heal);
        }
    }

    private void ApplySkills(NPC npc, BossState state)
    {
        state.SkillCooldown++;

        foreach (var skill in state.Skills)
        {
            switch (skill)
            {
                case "Enrage":
                    npc.damage = Math.Max(npc.damage, (int)(npc.defDamage * (1.15f + 0.1f * state.Phase)));
                    break;

                case "Defense":
                    npc.defense = Math.Max(npc.defense, (int)(npc.defDefense * (1.2f + 0.15f * state.Phase)));
                    break;

                case "Speed":
                    if (state.SkillCooldown % 30 == 0)
                        npc.velocity *= 1.05f + 0.03f * state.Phase;
                    break;

                case "HealBurst":
                    if (state.SkillCooldown % 600 == 0 && npc.life < state.MaxLife)
                    {
                        int heal = (int)(state.MaxLife * 0.03f * state.Phase);
                        npc.life = Math.Min(npc.life + heal, npc.lifeMax);
                        npc.HealEffect(heal);
                    }
                    break;

                case "Projectile":
                    if (state.SkillCooldown % 180 == 0 && Main.rand.NextBool(3))
                        TrySpawnSimpleProjectile(npc);
                    break;
            }
        }
    }

    private void TrySpawnSimpleProjectile(NPC npc)
    {
        try
        {
            int projType = 9;
            int damage = Math.Max(10, npc.damage / 3);
            var velocity = new Vector2(Main.rand.NextFloat(-6f, 6f), Main.rand.NextFloat(-6f, 6f));
            if (velocity.LengthSquared() < 1f)
                velocity = new Vector2(4f, 0f);

            int idx = Projectile.NewProjectile(
                null,
                npc.Center,
                velocity,
                projType,
                damage,
                1f,
                Main.myPlayer);

            if (idx >= 0 && idx < Main.maxProjectiles)
            {
                Main.projectile[idx].hostile = true;
                Main.projectile[idx].friendly = false;
            }
        }
        catch
        {
        }
    }

    private List<string> RollSkills(int phase)
    {
        var pool = new List<string> { "Enrage", "Defense", "Speed", "HealBurst", "Projectile" };
        int count = Math.Min(phase, pool.Count);
        return pool.OrderBy(_ => _rng.Next()).Take(count).ToList();
    }

    private string GenerateNickname(NPC npc)
    {
        string baseName = Lang.GetNPCNameValue(npc.type);
        string prefix = NickPrefixes[_rng.Next(NickPrefixes.Length)];
        string suffix = NickSuffixes[_rng.Next(NickSuffixes.Length)];
        return prefix + " " + baseName + " " + suffix;
    }

    private void CmdBossEvo(CommandArgs args)
    {
        if (args.Parameters.Count == 0)
        {
            args.Player.SendInfoMessage("/bossevo reload | /bossevo info");
            return;
        }

        switch (args.Parameters[0].ToLowerInvariant())
        {
            case "reload":
                LoadConfig();
                args.Player.SendSuccessMessage("BossEvolution config reloaded.");
                break;

            case "info":
                args.Player.SendInfoMessage("Enabled: " + _config.Enabled + ", Active bosses: " + _bosses.Count);
                args.Player.SendInfoMessage("Phase2 HP: " + _config.Phase2HpRatio.ToString("P0") +
                                            ", Phase3 HP: " + _config.Phase3HpRatio.ToString("P0"));
                break;

            default:
                args.Player.SendErrorMessage("Unknown subcommand.");
                break;
        }
    }
}

public class BossState
{
    public int NpcIndex { get; set; }
    public int NpcType { get; set; }
    public int Phase { get; set; } = 1;
    public string Nickname { get; set; } = "";
    public int MaxLife { get; set; }
    public List<string> Skills { get; set; } = new();
    public int SkillCooldown { get; set; }
}

public class Config
{
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("HpMultiplier")]
    public float HpMultiplier { get; set; } = 1.15f;

    [JsonPropertyName("Phase2HpRatio")]
    public float Phase2HpRatio { get; set; } = 0.70f;

    [JsonPropertyName("Phase3HpRatio")]
    public float Phase3HpRatio { get; set; } = 0.35f;

    [JsonPropertyName("PhaseHealPercent")]
    public float PhaseHealPercent { get; set; } = 0.05f;

    [JsonPropertyName("EnabledBossIds")]
    public List<int> EnabledBossIds { get; set; } = new();

    public static Config Default() => new();
}