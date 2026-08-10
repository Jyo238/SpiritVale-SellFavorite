using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SpiritValeSellFavorite
{
    /// <summary>
    /// 紫星「販賣保護收藏」：
    ///   遊戲原生黃星（hover + F）只做一般收藏，賣東西時完全不設防。
    ///   本插件在「商人販賣介面」加一套獨立的紫星標記，熱鍵與原生收藏相同（F，可改設定）：
    ///     ・人物端（左側自己的背包）按 F ⇒ 切換紫星；有紫星的物品無法加入販賣清單
    ///       （點擊、Sell All 全部擋下）
    ///     ・商人端（右側販賣清單）按 F ⇒ 直接標上紫星並退回背包
    ///   紫星狀態存於 BepInEx\config，重開遊戲不會消失。
    ///   全程只擋「client 端把物品加入販賣清單」的動作，不改數值、不偽造封包。
    /// </summary>
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BasePlugin
    {
        public const string GUID = "local.spiritvale.sellfavorite";
        public const string NAME = "SpiritVale Sell Favorite (紫星販賣保護)";
        public const string VERSION = "1.0.7";

        internal static ManualLogSource Logger;

        internal static ConfigEntry<KeyCode> CfgKey;
        internal static ConfigEntry<bool> CfgBlockDismantle;
        internal static ConfigEntry<bool> CfgDiagnostic;

        public override void Load()
        {
            Logger = base.Log;

            CfgKey = Config.Bind("1.操作", "紫星熱鍵", KeyCode.F,
                "商店販賣介面中，滑鼠停在物品上按此鍵切換紫星。預設 F 與遊戲原生收藏鍵相同；" +
                "原生黃星只在角色背包畫面作用，商店裡這顆鍵完全由本插件接手，互不打架。");
            CfgBlockDismantle = Config.Bind("1.操作", "同時保護分解", true,
                "true＝紫星物品在「分解」模式下也無法加入清單。");
            CfgDiagnostic = Config.Bind("2.診斷", "診斷模式", false,
                "把 hover 提示動作（文字＋按鍵）寫進 log，用來核對遊戲的收藏鍵設定。");

            Store.Init();

            var harmony = new Harmony(GUID);

            // ---- 商人販賣介面：實例快取 + 擋賣三重防線 ----
            TryPatch(harmony, "販賣介面實例快取(Awake)",
                () => AccessTools.Method(typeof(UIMerchantSell), "Awake"),
                postfix: nameof(Patches.SellAwake_Postfix));

            // 點擊加入與 SellAll 內部都走 CanSell → AddItem（ISIL 驗證），
            // 這裡只攔 AddItem 不動 CanSell —— 因為販賣畫面的顯示過濾器也吃 CanSell，
            // 動了 CanSell 會讓紫星物品像原生黃星一樣「整個從商店消失」，
            // 那就沒辦法在商店裡解除紫星了。紫星要保持可見、只擋加入清單。
            TryPatch(harmony, "擋賣防線1(AddItem)",
                () => AccessTools.Method(typeof(UIMerchantSell), nameof(UIMerchantSell.AddItem),
                    new[] { typeof(InventoryItemData), typeof(bool) }),
                prefix: nameof(Patches.AddItem_Prefix));

            TryPatch(harmony, "擋賣防線2(SellAll後清掃)",
                () => AccessTools.Method(typeof(UIMerchantSell), nameof(UIMerchantSell.SellAll)),
                postfix: nameof(Patches.SellAll_Postfix));

            // ---- 原生收藏鍵攔截：商店開啟時 F 不再切黃星，改走紫星 ----
            TryPatch(harmony, "原生收藏鍵攔截(ToggleFavorite)",
                () => AccessTools.Method(typeof(PlayerSave), nameof(PlayerSave.ToggleFavorite),
                    new[] { typeof(InventoryItemData) }),
                prefix: nameof(Patches.ToggleFavorite_Prefix));

            // ---- 紫星圖示：所有 Draw 多載共用一個 postfix（讀 __instance.Data 判定）----
            int drawCount = 0;
            foreach (var m in typeof(UIInventoryItem).GetMethods())
            {
                if (m.Name != "Draw") continue;
                try
                {
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(Patches), nameof(Patches.AnyDraw_Postfix)));
                    drawCount++;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[紫星圖示] Draw 多載掛載失敗，略過：{ex.Message}");
                }
            }
            Logger.LogInfo($"[紫星圖示] 已掛載 {drawCount} 個 Draw 多載。");

            TryPatch(harmony, "彈窗hover快取(UIItemPopup.Draw)",
                () => AccessTools.Method(typeof(UIItemPopup), nameof(UIItemPopup.Draw),
                    new[] { typeof(IInfoDrawable), typeof(GameObject), typeof(int) }),
                postfix: nameof(Patches.PopupDraw_Postfix));

            TryPatch(harmony, "紫星圖示(Clear)",
                () => AccessTools.Method(typeof(UIInventoryItem), nameof(UIInventoryItem.Clear)),
                postfix: nameof(Patches.Clear_Postfix));

            TryPatch(harmony, "紫星讓位黃星(SetFavorite)",
                () => AccessTools.Method(typeof(UIInventoryItem), nameof(UIInventoryItem.SetFavorite),
                    new[] { typeof(bool) }),
                postfix: nameof(Patches.SetFavorite_Postfix));

            if (CfgDiagnostic.Value)
            {
                TryPatch(harmony, "診斷(hover動作列表)",
                    () => AccessTools.Method(typeof(UIItemPopup), nameof(UIItemPopup.DrawInputActions)),
                    postfix: nameof(Patches.DrawInputActions_Postfix));
            }

            // ---- 熱鍵監聽：掛在遊戲現成的 UIManager.LateUpdate 上 ----
            // 不用 AddComponent 注入 MonoBehaviour —— Il2CppInterop 的類別注入 hook
            // （Class_FromIl2CppType / MetadataCache_GetTypeInfoFromTypeDefinitionIndex）
            // 在本遊戲（Unity 6000.0.64）會在場景載入時觸發 AccessViolation 崩潰。
            // 純 Harmony detour 沒有這個問題（SubstatHUD 同款手法已驗證）。
            TryPatch(harmony, "熱鍵監聽(UIManager.LateUpdate)",
                () => AccessTools.Method(typeof(UIManager), "LateUpdate"),
                postfix: nameof(Patches.UIManagerLateUpdate_Postfix));

            Logger.LogInfo($"{NAME} v{VERSION} 已載入。紫星數：{Store.Count}");
        }

        /// <summary>逐一掛載 patch，任一失敗只記警告不中斷 —— 遊戲改版時降級而非崩潰。</summary>
        private static void TryPatch(Harmony harmony, string label,
            Func<System.Reflection.MethodBase> resolver, string prefix = null, string postfix = null)
        {
            try
            {
                var target = resolver();
                if (target == null)
                {
                    Logger.LogWarning($"[{label}] 找不到目標方法，略過（遊戲版本可能已更新）。");
                    return;
                }

                harmony.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(Patches), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(Patches), postfix));

                Logger.LogInfo($"[{label}] 掛載成功。");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[{label}] 掛載失敗，略過：{ex.Message}");
            }
        }
    }

    internal static class Core
    {
        /// <summary>由 UIMerchantSell.Awake postfix 快取。</summary>
        internal static UIMerchantSell Sell;

        /// <summary>同一幀只處理一次收藏鍵（我們的 Update 與遊戲的 LateUpdate 可能都會觸發）。</summary>
        internal static int LastHandledFrame = -1;

        internal static bool ShopOpen
        {
            get
            {
                try { return Sell != null && Sell.gameObject.activeInHierarchy; }
                catch { return false; }
            }
        }

        internal static void OnUpdate()
        {
            if (!ShopOpen) return;
            if (!Input.GetKeyDown(Plugin.CfgKey.Value)) return;
            if (Time.frameCount == LastHandledFrame) return;
            if (IsTypingInInputField()) return;

            var slot = RaycastSlot();
            if (slot != null)
            {
                if (Plugin.CfgDiagnostic.Value)
                    Plugin.Logger.LogInfo($"[紫星] raycast 命中格子：{slot.gameObject.name}");
                HandleKeyOnSlot(slot);
                return;
            }

            // raycast 沒找到格子時，退而求其次用道具彈窗目前顯示的物品
            // （商人販賣清單的列有時攔不到 raycast，但 hover 中彈窗一定知道是誰）
            try
            {
                if (Popup != null && Popup.gameObject.activeInHierarchy && PopupData != null)
                {
                    var item = PopupData.TryCast<InventoryItemData>();
                    if (item != null)
                    {
                        if (Plugin.CfgDiagnostic.Value)
                            Plugin.Logger.LogInfo($"[紫星] 改用彈窗資料：{item.GetInstanceId()}");
                        HandleItem(item, null);
                    }
                }
            }
            catch { }
        }

        /// <summary>由 UIItemPopup.Draw postfix 快取：目前 hover 中的物品。</summary>
        internal static UIItemPopup Popup;
        internal static IInfoDrawable PopupData;

        /// <summary>搜尋欄有焦點時不要搶按鍵。</summary>
        private static bool IsTypingInInputField()
        {
            try
            {
                var es = EventSystem.current;
                var sel = es != null ? es.currentSelectedGameObject : null;
                return sel != null && sel.GetComponent<TMP_InputField>() != null;
            }
            catch { return false; }
        }

        /// <summary>找出滑鼠正下方的物品格。</summary>
        private static UIInventoryItem RaycastSlot()
        {
            var es = EventSystem.current;
            if (es == null) return null;

            var ped = new PointerEventData(es);
            var mp = Input.mousePosition;
            ped.position = new Vector2(mp.x, mp.y);

            var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
            es.RaycastAll(ped, results);

            for (int i = 0; i < results.Count; i++)
            {
                var go = results[i].gameObject;
                if (go == null) continue;
                var slot = go.GetComponentInParent<UIInventoryItem>();
                if (slot != null) return slot;
            }
            return null;
        }

        /// <summary>
        /// 判定「在不在販賣清單」：直接迭代 Transaction.Items，用 InstanceId 字串比對，
        /// 回傳字典裡實際儲存的那個物件（加入清單時物品可能被複製，
        /// 拿畫面上的物件去給 Count()/RemoveItem() 比對會失敗 —— v1.0.2 bug）。
        /// </summary>
        private static InventoryItemData FindInSellList(string id)
        {
            bool diag = Plugin.CfgDiagnostic.Value;
            try
            {
                var tx = Sell != null ? Sell.Transaction : null;
                if (tx == null)
                {
                    if (diag) Plugin.Logger.LogInfo($"[紫星][診斷] Sell={(Sell == null ? "null" : "ok")} Transaction=null");
                    return null;
                }
                var dict = tx.Items;
                if (dict == null)
                {
                    if (diag) Plugin.Logger.LogInfo("[紫星][診斷] Transaction.Items=null");
                    return null;
                }

                if (diag) Plugin.Logger.LogInfo($"[紫星][診斷] 目標id={id}，清單共 {dict.Count} 筆：");
                foreach (var kv in dict)
                {
                    var entry = kv.Value;
                    if (entry == null || entry.Item == null)
                    {
                        if (diag) Plugin.Logger.LogInfo("  [entry null]");
                        continue;
                    }
                    string eid = entry.Item.GetInstanceId();
                    if (diag) Plugin.Logger.LogInfo($"  entry id={eid} count={entry.Count}");
                    if (string.Equals(eid, id, StringComparison.Ordinal))
                        return entry.Item;
                }
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"檢查販賣清單失敗：{ex.Message}"); }
            return null;
        }

        /// <summary>
        /// 商人端（已在販賣清單）：標上紫星並用遊戲原生 RemoveItem 退回背包。
        /// 人物端：切換紫星。slot 可為 null（ToggleFavorite 路徑沒有格子資訊）。
        /// </summary>
        internal static void HandleItem(InventoryItemData item, UIInventoryItem slot)
        {
            string id = item.GetInstanceId();
            if (string.IsNullOrEmpty(id)) return;

            LastHandledFrame = Time.frameCount;

            var stored = FindInSellList(id);
            if (stored != null)
            {
                Store.Add(id);
                try
                {
                    // 遊戲的 AddItem/RemoveItem 只改資料不刷畫面，
                    // 原生點擊路徑（<Awake>b__9_0）是 mutate 完再補 Redraw —— 照抄。
                    Sell.RemoveItem(stored);
                    Sell.Redraw(stored);
                }
                catch (Exception ex) { Plugin.Logger.LogWarning($"退回物品失敗：{ex.Message}"); }
                if (Plugin.CfgDiagnostic.Value)
                    Plugin.Logger.LogInfo($"[紫星] 商人端退回：{id}");
            }
            else
            {
                Store.Toggle(id);
                if (slot != null) PurpleIcon.Apply(slot, Store.Contains(id) && !item.Favorite);
                if (Plugin.CfgDiagnostic.Value)
                    Plugin.Logger.LogInfo($"[紫星] 人物端切換：{id} => {Store.Contains(id)}");
            }

            // 讓背包側重畫（圖示由 Draw postfix 統一補上／清除）
            try { Sell?.Inventory?.Redraw(id); } catch { }
        }

        internal static void HandleKeyOnSlot(UIInventoryItem slot)
        {
            var data = slot.Data;
            if (data == null) return;

            var item = data.TryCast<InventoryItemData>();
            if (item == null) return;

            HandleItem(item, slot);
        }

        /// <summary>ToggleFavorite prefix 進來的路徑（遊戲自己解析好 hover 的物品）。</summary>
        internal static void HandleToggleFromGame(InventoryItemData item)
        {
            HandleItem(item, null);
        }
    }

    internal static class Patches
    {
        // ---- 販賣介面實例快取 ----
        public static void SellAwake_Postfix(UIMerchantSell __instance)
        {
            Core.Sell = __instance;
        }

        // ---- 每幀熱鍵檢查（借 UIManager 的 LateUpdate，不注入自己的元件）----
        public static void UIManagerLateUpdate_Postfix()
        {
            try { Core.OnUpdate(); }
            catch (Exception ex) { Plugin.Logger.LogWarning($"熱鍵處理失敗：{ex.Message}"); }
        }

        // ---- 彈窗 hover 快取：raycast 找不到格子時的備援資料來源 ----
        public static void PopupDraw_Postfix(UIItemPopup __instance, IInfoDrawable data)
        {
            Core.Popup = __instance;
            Core.PopupData = data;
        }

        // ---- 防線 1：點擊加入販賣清單 ----
        public static bool AddItem_Prefix(UIMerchantSell __instance, InventoryItemData item)
        {
            try
            {
                if (item == null) return true;
                if (!Store.Contains(item.GetInstanceId())) return true;
                if (__instance.IsDismantle && !Plugin.CfgBlockDismantle.Value) return true;
                return false;   // 紫星物品：直接擋下
            }
            catch { return true; }
        }

        // ---- 防線 2：SellAll 後把漏網的紫星物品清出清單（保險用，正常情況防線 1 已全擋）----
        public static void SellAll_Postfix(UIMerchantSell __instance)
        {
            try
            {
                var tx = __instance.Transaction;
                if (tx == null) return;
                var dict = tx.Items;
                if (dict == null) return;

                var toRemove = new List<InventoryItemData>();
                foreach (var kv in dict)
                {
                    var entry = kv.Value;
                    if (entry == null || entry.Item == null) continue;
                    if (Store.Contains(entry.Item.GetInstanceId())) toRemove.Add(entry.Item);
                }
                foreach (var it in toRemove)
                    __instance.RemoveItem(it);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"SellAll 清掃失敗：{ex.Message}"); }
        }

        // ---- 原生收藏鍵：商店開啟時改走紫星，不動黃星 ----
        public static bool ToggleFavorite_Prefix(InventoryItemData item)
        {
            try
            {
                if (!Core.ShopOpen)
                {
                    // 一般背包：黃星「取代」紫星 —— 按下收藏鍵的瞬間清除紫星標記。
                    // 流轉：商店標紫 → 背包按收藏＝紫變黃 → 再按一次＝全部歸零。
                    if (item != null)
                    {
                        string fid = item.GetInstanceId();
                        if (Store.Contains(fid))
                        {
                            Store.Remove(fid);
                            Plugin.Logger.LogInfo($"[紫星] 黃星取代，已清除紫星：{fid}");
                        }
                    }
                    return true;    // 維持原生黃星行為
                }
                if (item == null) return false;

                // 我們的 Update 已處理過這一幀 ⇒ 只擋黃星，不重複切換
                if (Time.frameCount != Core.LastHandledFrame)
                    Core.HandleToggleFromGame(item);

                return false;
            }
            catch { return true; }
        }

        // ---- 紫星圖示 ----
        // 黃星（原生收藏）優先級較高：兩者同位置疊圖會混色，且黃星物品本來就
        // 不可販賣（CanSell 原生擋下），紫星顯示讓位不損失任何保護語義。
        // 紫星「資料」保留 —— 取消黃星後紫星立刻浮回來。
        public static void AnyDraw_Postfix(UIInventoryItem __instance)
        {
            try
            {
                var data = __instance.Data;
                var item = data != null ? data.TryCast<InventoryItemData>() : null;
                bool marked = item != null && Store.Contains(item.GetInstanceId());
                bool purple = marked && !item.Favorite;
                if (marked && Plugin.CfgDiagnostic.Value)
                    Plugin.Logger.LogInfo($"[紫星][診斷] AnyDraw slot={__instance.gameObject.name} id={item.GetInstanceId()} fav={item.Favorite} => purple={purple}");
                PurpleIcon.Apply(__instance, purple);
            }
            catch { }
        }

        // 遊戲單獨呼叫 SetFavorite（不走完整 Draw）時也要即時讓位／浮回
        public static void SetFavorite_Postfix(UIInventoryItem __instance, bool favorite)
        {
            try
            {
                var data = __instance.Data;
                var item = data != null ? data.TryCast<InventoryItemData>() : null;
                bool marked = item != null && Store.Contains(item.GetInstanceId());
                if (marked && Plugin.CfgDiagnostic.Value)
                    Plugin.Logger.LogInfo($"[紫星][診斷] SetFavorite slot={__instance.gameObject.name} favorite={favorite} id={item.GetInstanceId()}");
                if (favorite)
                {
                    PurpleIcon.Apply(__instance, false);
                    return;
                }
                PurpleIcon.Apply(__instance, marked);
            }
            catch { }
        }

        public static void Clear_Postfix(UIInventoryItem __instance)
        {
            try { PurpleIcon.Apply(__instance, false); }
            catch { }
        }

        // ---- 診斷：印出 hover 提示動作（文字＋按鍵）----
        private static readonly HashSet<string> _loggedActions = new HashSet<string>();

        public static void DrawInputActions_Postfix(
            Il2CppSystem.Collections.Generic.List<UIInputActionConfig> actions)
        {
            try
            {
                if (actions == null) return;
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < actions.Count; i++)
                {
                    var a = actions[i];
                    if (a == null) continue;
                    sb.Append('[').Append(a.GetText()).Append(" = ").Append(a.Key).Append("] ");
                }
                string line = sb.ToString();
                if (line.Length > 0 && _loggedActions.Add(line))
                    Plugin.Logger.LogInfo("hover 動作：" + line);
            }
            catch { }
        }
    }

    /// <summary>紫星標記的持久化：一行一個物品 InstanceId。</summary>
    internal static class Store
    {
        private static readonly HashSet<string> _ids = new HashSet<string>(StringComparer.Ordinal);
        private static string _path;

        internal static int Count => _ids.Count;

        internal static void Init()
        {
            _path = Path.Combine(Paths.ConfigPath, "local.spiritvale.sellfavorite.marks.txt");
            try
            {
                if (File.Exists(_path))
                    foreach (var line in File.ReadAllLines(_path))
                        if (!string.IsNullOrWhiteSpace(line)) _ids.Add(line.Trim());
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"讀取紫星清單失敗：{ex.Message}"); }
        }

        internal static bool Contains(string id) => !string.IsNullOrEmpty(id) && _ids.Contains(id);

        internal static void Add(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (_ids.Add(id)) Save();
        }

        internal static void Remove(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (_ids.Remove(id)) Save();
        }

        internal static void Toggle(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (!_ids.Add(id)) _ids.Remove(id);
            Save();
        }

        private static void Save()
        {
            try
            {
                var arr = new string[_ids.Count];
                _ids.CopyTo(arr);
                File.WriteAllLines(_path, arr);
            }
            catch (Exception ex) { Plugin.Logger.LogWarning($"寫入紫星清單失敗：{ex.Message}"); }
        }
    }

    /// <summary>
    /// 紫星圖示：在黃星（UIInventoryItem.Favorite）旁邊放一個紫色 ★（TMP 文字，
    /// 不複製 sprite 是因為黃色貼圖染紫會變濁色，文字星形可保證正紫）。
    /// 物品格會被 object pooling 重複利用，圖示以名稱查找、跟著格子開關。
    /// </summary>
    internal static class PurpleIcon
    {
        private const string GoName = "PurpleSellFavMark";
        private static readonly Color PurpleColor = new Color(0.72f, 0.35f, 1.00f);

        internal static void Apply(UIInventoryItem slot, bool on)
        {
            try
            {
                if (slot == null) return;
                var fav = slot.Favorite;
                if (fav == null)
                {
                    if (on && Plugin.CfgDiagnostic.Value)
                        Plugin.Logger.LogInfo($"[紫星][診斷] slot={slot.gameObject.name} 沒有 Favorite 物件，無法畫紫星");
                    return;
                }
                var parent = fav.transform.parent;
                if (parent == null) return;

                var t = parent.Find(GoName);
                if (t == null)
                {
                    if (!on) return;
                    t = Create(slot, fav, parent);
                    if (t == null) return;
                }
                t.gameObject.SetActive(on);

                if (on && Plugin.CfgDiagnostic.Value)
                {
                    var go = t.gameObject;
                    string blocker = "";
                    if (!go.activeInHierarchy)
                    {
                        var cur = t.parent;
                        while (cur != null)
                        {
                            if (!cur.gameObject.activeSelf) { blocker = cur.gameObject.name; break; }
                            cur = cur.parent;
                        }
                    }
                    var rt = t.GetComponent<RectTransform>();
                    Plugin.Logger.LogInfo(
                        $"[紫星][診斷] icon slot={slot.gameObject.name} activeSelf={go.activeSelf} " +
                        $"inHierarchy={go.activeInHierarchy} 被誰蓋={(blocker == "" ? "無" : blocker)} " +
                        $"favActive={fav.activeSelf} parent={parent.gameObject.name} " +
                        $"pos={(rt != null ? rt.anchoredPosition.ToString() : "?")} size={(rt != null ? rt.sizeDelta.ToString() : "?")}");
                }
            }
            catch { }
        }

        private static Transform Create(UIInventoryItem slot, GameObject fav, Transform parent)
        {
            var go = new GameObject(GoName);
            go.transform.SetParent(parent, false);

            var rt = go.AddComponent<RectTransform>();
            var favRt = fav.GetComponent<RectTransform>();
            if (favRt != null)
            {
                // 與原生星號完全同位置。之前左移一格寬會讓星星跑出格子外，
                // 看起來像標到隔壁的物品（v1.0.1 bug）。
                rt.anchorMin = favRt.anchorMin;
                rt.anchorMax = favRt.anchorMax;
                rt.pivot = favRt.pivot;
                rt.sizeDelta = favRt.sizeDelta;
                rt.anchoredPosition = favRt.anchoredPosition;
            }
            go.transform.SetAsLastSibling();

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = "★";
            tmp.color = PurpleColor;
            try
            {
                if (slot.Name != null && slot.Name.font != null) tmp.font = slot.Name.font;
            }
            catch { }
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMin = 6f;
            tmp.fontSizeMax = 60f;
            tmp.raycastTarget = false;

            return go.transform;
        }
    }
}
