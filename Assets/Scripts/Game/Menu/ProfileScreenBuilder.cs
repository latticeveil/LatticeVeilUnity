using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using LatticeVeil.Core;

namespace LatticeVeil.Game
{
    /// <summary>
    /// PROFILE screen — Unity port of the MonoGame ProfileScreen with a
    /// modern finish: Veilnet identity (avatar, banner, about-me from
    /// Supabase via VeilnetClient), stats block, tabs IDENTITY / FRIENDS /
    /// REQUESTS, friend list rows with cached/online avatar thumbnails,
    /// add-friend input, remove/accept/decline actions.
    /// Notifications (friend requests + world invites) show as toasts pinned
    /// top-left with a hold-Y/hold-N quick reply.
    /// </summary>
    public static class ProfileScreenBuilder
    {
        // Frame metrics (same as Screenshots/Options).
        private const float LeftEdge = -424f;
        private const float RightEdge = 424f;
        private const float HeaderY = 224f;

        private static readonly Color TextColor = Color.white;
        private static readonly Color SubText = new Color(0.63f, 0.67f, 0.73f, 1f);
        private static readonly Color Accent = new Color(124f / 255f, 92f / 255f, 1f);
        private static readonly Color AccentSoft = new Color(0.49f, 0.36f, 1f, 0.35f);
        private static readonly Color RowBg = new Color(18f / 255f, 20f / 255f, 26f / 255f, 0.95f);
        private static readonly Color ActionBg = new Color(24f / 255f, 28f / 255f, 36f / 255f);
        private static readonly Color ActionBorder = new Color(140f / 255f, 150f / 255f, 165f / 255f);

        private static RectTransform _root;
        private static RectTransform _scrollContent;
        private static RectTransform _pageRoot;   // current tab page
        private static Text _statusText;
        private static ProfileScreenController _controller;
        private static Button _acceptBtn;
        private static Button _declineBtn;
        private static Button _removeBtn;
        private static InputField _addInput;

        public static ProfileScreenController Open(Canvas canvas, Action onClosed)
        {
            var go = new GameObject("ProfileScreen", typeof(RectTransform), typeof(ProfileScreenController));
            _root = (RectTransform)go.transform;
            _root.SetParent(canvas.transform, false);
            Stretch(_root);

            // Dim background.
            var dimGo = new GameObject("Dim", typeof(RectTransform), typeof(Image));
            var dim = (RectTransform)dimGo.transform;
            dim.SetParent(_root, false);
            Stretch(dim);
            dimGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);

            // Frame panel (Settings_GUI art like the other screens).
            var frameGo = new GameObject("Frame", typeof(RectTransform), typeof(Image));
            var frame = (RectTransform)frameGo.transform;
            frame.SetParent(_root, false);
            frame.anchorMin = new Vector2(0.5f, 0.5f);
            frame.anchorMax = new Vector2(0.5f, 0.5f);
            frame.pivot = new Vector2(0.5f, 0.5f);
            frame.sizeDelta = new Vector2(960f, 540f);
            var frameTex = GameAssetLoader.LoadTexture("textures/menu/GUIS/Settings_GUI.png");
            if (frameTex != null)
            {
                frameGo.GetComponent<Image>().sprite = Sprite.Create(frameTex,
                    new Rect(0, 0, frameTex.width, frameTex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            else
            {
                frameGo.GetComponent<Image>().color = RowBg;
            }

            var controller = go.GetComponent<ProfileScreenController>();
            _controller = controller;

            // ---- Header: title + status right ----
            var title = CreateLabel(frame, "PROFILE", 18, TextColor);
            SetRect((RectTransform)title.transform, new Vector2(LeftEdge + 140f, HeaderY), new Vector2(300f, 26f));
            title.alignment = TextAnchor.MiddleLeft;

            _statusText = CreateLabel(frame, "", 11, SubText);
            SetRect((RectTransform)_statusText.transform, new Vector2(0f, HeaderY - 20f), new Vector2(RightEdge - LeftEdge, 14f));
            _statusText.alignment = TextAnchor.MiddleCenter;

            // ---- Tab row (marginTop) ----
            string[] tabNames = { "IDENTITY", "FRIENDS", "REQUESTS" };
            float tabW = 160f, tabGap = 12f, tabsX = LeftEdge + 60f;
            for (int i = 0; i < 3; i++)
            {
                var captured = i;
                var btn = CreateTabButton(frame, tabNames[i], tabsX + i * (tabW + tabGap), HeaderY - 46f, tabW, 30f, () =>
                {
                    controller.SelectTab(captured);
                });
                controller.RegisterTab(i, btn);
            }

            // ---- Scrolling content area (inside the frame, below tabs) ----
            var viewportGo = new GameObject("Content", typeof(RectTransform), typeof(RectMask2D));
            var viewport = (RectTransform)viewportGo.transform;
            viewport.SetParent(frame, false);
            viewport.anchorMin = new Vector2(0f, 0f);
            viewport.anchorMax = new Vector2(1f, 1f);
            viewport.pivot = new Vector2(0.5f, 0.5f);
            viewport.offsetMin = new Vector2(56f, 64f);
            viewport.offsetMax = new Vector2(-56f, -128f);

            var scroll = viewportGo.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 28f;
            viewportGo.AddComponent<OptionsScreenBuilder.ScrollAnywhereDrag>().Scroll = scroll;

            // Visible scrollbar inside the right frame cut (same geometry as
            // Options) so dragging/reading position works and the wheel
            // response matches the other screens.
            var scrollbarGo = new GameObject("Scrollbar", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
            var scrollbarRect = (RectTransform)scrollbarGo.transform;
            scrollbarRect.SetParent(frame, false);
            scrollbarRect.anchorMin = new Vector2(1f, 0f);
            scrollbarRect.anchorMax = new Vector2(1f, 1f);
            scrollbarRect.pivot = new Vector2(1f, 0.5f);
            scrollbarRect.offsetMin = new Vector2(-76f, 64f);
            scrollbarRect.offsetMax = new Vector2(-62f, -128f);
            scrollbarGo.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.14f);
            var scrollbar = scrollbarGo.GetComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            var slidingArea = new GameObject("Sliding Area", typeof(RectTransform));
            var slidingRect = (RectTransform)slidingArea.transform;
            slidingRect.SetParent(scrollbarRect, false);
            Stretch(slidingRect);
            slidingRect.offsetMin = new Vector2(3f, 4f);
            slidingRect.offsetMax = new Vector2(-3f, -4f);
            var handleGo = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            var handleRect = (RectTransform)handleGo.transform;
            handleRect.SetParent(slidingRect, false);
            Stretch(handleRect);
            handleGo.GetComponent<Image>().color = new Color(0.85f, 0.9f, 0.95f, 0.75f);
            scrollbar.handleRect = handleRect;
            scrollbar.targetGraphic = handleGo.GetComponent<Image>();
            scroll.verticalScrollbar = scrollbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
            scroll.verticalScrollbarSpacing = 4f;

            var contentGo = new GameObject("PageContent", typeof(RectTransform));
            _scrollContent = (RectTransform)contentGo.transform;
            _scrollContent.SetParent(viewport, false);
            _scrollContent.anchorMin = new Vector2(0f, 1f);
            _scrollContent.anchorMax = new Vector2(1f, 1f);
            _scrollContent.pivot = new Vector2(0.5f, 1f);
            _scrollContent.anchoredPosition = Vector2.zero;
            _scrollContent.sizeDelta = new Vector2(0f, 300f);
            scroll.content = _scrollContent;
            scroll.viewport = viewport;

            // First page is built after controller.Init wires _content (Refresh).

            // Shrink-fitted BACK at bottom-left (same style as Screenshots).
            var backGo = new GameObject("Btn_BACK", typeof(RectTransform), typeof(Image), typeof(Button));
            var backRect = (RectTransform)backGo.transform;
            backRect.SetParent(_root, false);
            backRect.anchorMin = new Vector2(0f, 0f);
            backRect.anchorMax = new Vector2(0f, 0f);
            backRect.pivot = new Vector2(0f, 0f);
            backRect.sizeDelta = new Vector2(140f, 40f);
            backRect.anchoredPosition = new Vector2(10f, 10f);
            var backTex = GameAssetLoader.LoadTexture("textures/menu/buttons/Back.png");
            if (backTex != null)
            {
                var bImg = backGo.GetComponent<Image>();
                bImg.sprite = Sprite.Create(backTex, new Rect(0, 0, backTex.width, backTex.height),
                    new Vector2(0.5f, 0.5f), 100f);
                bImg.preserveAspect = true;
                backRect.sizeDelta = new Vector2(156f, 40f);
            }
            else
            {
                backGo.GetComponent<Image>().color = ActionBg;
                var bOutline = backGo.AddComponent<Outline>();
                bOutline.effectColor = ActionBorder;
                bOutline.effectDistance = new Vector2(1.5f, -1.5f);
                var bLabel = CreateLabel(backRect, "BACK", 15, TextColor);
                Stretch(bLabel.transform as RectTransform);
                bLabel.alignment = TextAnchor.MiddleCenter;
            }
            backGo.GetComponent<Button>().onClick.AddListener(() => controller.Close());

            controller.Init(_root, _scrollContent, _statusText);
            controller.SetClosed(onClosed);
            controller.Refresh();
            return controller;
        }

        // ------------------------------------------------------------------
        // Tab pages
        // ------------------------------------------------------------------

        public static void BuildIdentityPage(RectTransform content, ProfileScreenController controller)
        {
            ClearPage(content);

            var user = controller.CurrentProfile ?? new VeilnetClient.GateUser();
            float y = 0f;

            // Banner (loader loads async, aspect-filled).
            var bannerGo = new GameObject("Banner", typeof(RectTransform), typeof(Image), typeof(Outline));
            var bannerRect = (RectTransform)bannerGo.transform;
            bannerRect.SetParent(content, false);
            bannerRect.anchorMin = new Vector2(0.5f, 1f);
            bannerRect.anchorMax = new Vector2(0.5f, 1f);
            bannerRect.pivot = new Vector2(0.5f, 1f);
            bannerRect.sizeDelta = new Vector2(720f, 180f);
            bannerRect.anchoredPosition = new Vector2(0f, -y);
            bannerGo.GetComponent<Image>().color = new Color(30f / 255f, 34f / 255f, 45f / 255f, 1f);
            bannerGo.GetComponent<Outline>().effectColor = AccentSoft;
            bannerGo.GetComponent<Outline>().effectDistance = new Vector2(2f, -2f);
            if (!string.IsNullOrWhiteSpace(user.BannerUrl))
                bannerGo.AddComponent<ImageDownloader>().Load(user.BannerUrl, bannerRect, ImageDownloader.FillMode.AspectFill);

            // Banner overlay text (display name + @username).
            var dName = CreateLabel(bannerRect, string.IsNullOrWhiteSpace(user.DisplayName) ? "NO IDENTITY" : user.DisplayName, 26, TextColor);
            SetRect((RectTransform)dName.transform, new Vector2(40f, -124f), new Vector2(600f, 32f));
            dName.alignment = TextAnchor.LowerLeft;
            var uName = CreateLabel(bannerRect, "@" + (string.IsNullOrWhiteSpace(user.Username) ? "signed_out" : user.Username), 14, SubText);
            SetRect((RectTransform)uName.transform, new Vector2(40f, -152f), new Vector2(600f, 20f));
            uName.alignment = TextAnchor.MiddleLeft;

            // Avatar tile (or placeholder monogram) over the banner's left.
            var avatarGo = new GameObject("Avatar", typeof(RectTransform), typeof(Image), typeof(Outline));
            var avatarRect = (RectTransform)avatarGo.transform;
            avatarRect.SetParent(content, false);
            avatarRect.anchorMin = new Vector2(0.5f, 1f);
            avatarRect.anchorMax = new Vector2(0.5f, 1f);
            avatarRect.pivot = new Vector2(0f, 1f);
            avatarRect.sizeDelta = new Vector2(120f, 120f);
            avatarRect.anchoredPosition = new Vector2(-380f, -60f);
            avatarGo.GetComponent<Image>().color = new Color(40f / 255f, 44f / 255f, 58f / 255f, 1f);
            avatarGo.GetComponent<Outline>().effectColor = Accent;
            avatarGo.GetComponent<Outline>().effectDistance = new Vector2(2f, -2f);
            if (!string.IsNullOrWhiteSpace(user.PictureUrl))
                avatarGo.AddComponent<ImageDownloader>().Load(user.PictureUrl, avatarRect, ImageDownloader.FillMode.AspectFill);
            else
            {
                var mono = CreateLabel(avatarRect, Monogram(user.Username, user.DisplayName), 40, Accent);
                Stretch(mono.transform as RectTransform);
                mono.alignment = TextAnchor.MiddleCenter;
            }

            y += 200f;

            // About-me card.
            var aboutCard = CreateCard(content, new Vector2(0f, -y), new Vector2(720f, 110f), "ABOUT ME");
            var aboutText = aboutCard.GetComponentInChildren<Text>();
            if (aboutText != null) aboutText.text = string.IsNullOrWhiteSpace(user.AboutMe)
                ? "No about-me set yet. Edit your profile in Veilnet to fill this in."
                : user.AboutMe;
            y += 124f;

            // Stats card.
            var stats = CreateCard(content, new Vector2(0f, -y), new Vector2(720f, 96f), "STATUS");
            var statText = stats.GetComponentInChildren<Text>();
            if (statText != null)
            {
                statText.text = $"Friend code: {(string.IsNullOrWhiteSpace(user.FriendCode) ? "—" : user.FriendCode)}\n" +
                                $"Signed in: {(VeilnetClient.HasToken ? "YES · synced with Veilnet" : "not signed in")}";
            }
            y += 110f;

            // Edit profile (opens Veilnet web) + sign-out / sign-in button.
            CreateActionButton(content, "EDIT ON VEILNET", new Vector2(-180f, -y), new Vector2(200f, 30f), () => OpenVeilnetProfile(user.Username));
            CreateActionButton(content, VeilnetClient.HasToken ? "REFRESH PROFILE" : "SIGN IN REQUIRED", new Vector2(60f, -y), new Vector2(220f, 30f), controller.Refresh);
            y += 44f;

            content.sizeDelta = new Vector2(0f, y + 10f);
        }

        public static void BuildFriendsPage(RectTransform content, ProfileScreenController controller)
        {
            ClearPage(content);

            // Add friend: input + button.
            _addInput = CreateInputField(content, "FRIEND NAME OR CODE", new Vector2(-170f, 0f), new Vector2(460f, 30f));
            CreateActionButton(content, "ADD FRIEND", new Vector2(170f, -15f), new Vector2(160f, 30f), () => controller.AddFriend(_addInput.text));
            float y = 44f;

            var friends = controller.Friends;
            if (friends == null || friends.Count == 0)
            {
                var empty = CreateLabel(content, "NO FRIENDS YET. ADD SOME ABOVE.", 13, SubText);
                SetRect((RectTransform)empty.transform, new Vector2(0f, -y - 40f), new Vector2(720f, 20f));
                empty.alignment = TextAnchor.MiddleCenter;
                content.sizeDelta = new Vector2(0f, y + 100f);
                return;
            }

            for (int i = 0; i < friends.Count; i++)
            {
                var friend = friends[i];
                var idx = i;
                var row = CreateFriendRow(content, friend, y, () => controller.RemoveFriend(friend.ProductUserId));
                // Black out the row click (open profile overlay in MonoGame; here: show about).
                y += 56f;
            }
            content.sizeDelta = new Vector2(0f, y);
        }

        public static void BuildRequestsPage(RectTransform content, ProfileScreenController controller)
        {
            ClearPage(content);

            float y = 0f;
            var incoming = controller.Incoming;
            var outgoing = controller.Outgoing;

            var inTitle = CreateLabel(content, $"INCOMING ({incoming.Count})", 14, Accent);
            SetRect((RectTransform)inTitle.transform, new Vector2(-380f, -y - 14f), new Vector2(600f, 20f));
            y += 34f;

            if (incoming.Count == 0)
            {
                var e = CreateLabel(content, "None — hold Y/N on the toast also works.", 12, SubText);
                SetRect((RectTransform)e.transform, new Vector2(-370f, -y - 12f), new Vector2(600f, 16f));
                y += 30f;
            }
            for (int i = 0; i < incoming.Count; i++)
            {
                var r = incoming[i];
                var idx = i;
                var rowGo = CreateRequestRow(content, r.User?.Username ?? r.ProductUserId, r.User?.PictureUrl, y,
                    () => controller.RespondRequest(r.ProductUserId, true),
                    () => controller.RespondRequest(r.ProductUserId, false));
                y += 56f;
            }

            y += 14;
            var outTitle = CreateLabel(content, $"OUTGOING ({outgoing.Count})", 14, SubText);
            SetRect((RectTransform)outTitle.transform, new Vector2(-380f, -y - 14f), new Vector2(600f, 20f));
            y += 34f;

            if (outgoing.Count == 0)
            {
                var e = CreateLabel(content, "No pending sent requests.", 12, SubText);
                SetRect((RectTransform)e.transform, new Vector2(-370f, -y - 12f), new Vector2(600f, 16f));
                y += 30f;
            }
            for (int i = 0; i < outgoing.Count; i++)
            {
                var r = outgoing[i];
                var row = CreateLabel(content, r.User?.Username ?? r.ProductUserId, 13, TextColor);
                SetRect((RectTransform)row.transform, new Vector2(-370f, -y - 18f), new Vector2(500f, 22f));
                row.alignment = TextAnchor.MiddleLeft;
                var pending = CreateLabel(content, "PENDING", 11, SubText);
                SetRect((RectTransform)pending.transform, new Vector2(300f, -y - 18f), new Vector2(70f, 18f));
                y += 34f;
            }

            content.sizeDelta = new Vector2(0f, y + 20f);
        }

        // ------------------------------------------------------------------
        // Row/card helpers
        // ------------------------------------------------------------------

        private static RectTransform CreateFriendRow(RectTransform content, VeilnetClient.GateUser friend, float y, Action onRemove)
        {
            var rowGo = new GameObject("FriendRow_" + friend.Username, typeof(RectTransform), typeof(Image), typeof(Outline));
            var row = (RectTransform)rowGo.transform;
            row.SetParent(content, false);
            row.anchorMin = new Vector2(0.5f, 1f);
            row.anchorMax = new Vector2(0.5f, 1f);
            row.pivot = new Vector2(0.5f, 1f);
            row.sizeDelta = new Vector2(720f, 50f);
            row.anchoredPosition = new Vector2(0f, -y);
            rowGo.GetComponent<Image>().color = RowBg;
            rowGo.GetComponent<Outline>().effectColor = new Color(70f / 255f, 75f / 255f, 85f / 255f, 1f);
            rowGo.GetComponent<Outline>().effectDistance = new Vector2(1.5f, -1.5f);

            // Avatar (placeholder monogram until the image loads).
            var avGo = new GameObject("Avatar", typeof(RectTransform), typeof(Image), typeof(Outline));
            var av = (RectTransform)avGo.transform;
            av.SetParent(row, false);
            av.anchorMin = new Vector2(0f, 0.5f);
            av.anchorMax = new Vector2(0f, 0.5f);
            av.pivot = new Vector2(0f, 0.5f);
            av.sizeDelta = new Vector2(40f, 40f);
            av.anchoredPosition = new Vector2(6f, 0f);
            avGo.GetComponent<Image>().color = new Color(40f / 255f, 44f / 255f, 58f / 255f, 1f);
            avGo.GetComponent<Outline>().effectColor = new Color(96f / 255f, 104f / 255f, 116f / 255f, 1f);
            avGo.GetComponent<Outline>().effectDistance = new Vector2(1f, -1f);

            bool hasImg = !string.IsNullOrWhiteSpace(friend.PictureUrl);
            var monoLabel = CreateLabel(av, Monogram(friend.Username, friend.DisplayName), 18, Accent);
            Stretch(monoLabel.transform as RectTransform);
            monoLabel.alignment = TextAnchor.MiddleCenter;
            if (hasImg)
                avGo.AddComponent<ImageDownloader>().Load(friend.PictureUrl, av, ImageDownloader.FillMode.AspectFill, hideLabel: monoLabel.gameObject);

            var name = CreateLabel(row, string.IsNullOrWhiteSpace(friend.Username) ? friend.ProductUserId : friend.Username, 15, TextColor);
            SetRect((RectTransform)name.transform, new Vector2(-310f, 6f), new Vector2(500f, 20f));
            name.alignment = TextAnchor.MiddleLeft;

            var sub = CreateLabel(row, string.IsNullOrWhiteSpace(friend.AboutMe) ? "Friend" : friend.AboutMe, 11, SubText);
            SetRect((RectTransform)sub.transform, new Vector2(-310f, -12f), new Vector2(560f, 16f));
            sub.alignment = TextAnchor.MiddleLeft;
            sub.fontSize = 10;

            CreateActionButton(row, "REMOVE", new Vector2(300f, 0f), new Vector2(110f, 26f), onRemove);
            return row;
        }

        private static RectTransform CreateRequestRow(RectTransform content, string username, string pictureUrl, float y, Action onAccept, Action onDecline)
        {
            var rowGo = new GameObject("RequestRow_" + username, typeof(RectTransform), typeof(Image), typeof(Outline));
            var row = (RectTransform)rowGo.transform;
            row.SetParent(content, false);
            row.anchorMin = new Vector2(0.5f, 1f);
            row.anchorMax = new Vector2(0.5f, 1f);
            row.pivot = new Vector2(0.5f, 1f);
            row.sizeDelta = new Vector2(720f, 46f);
            row.anchoredPosition = new Vector2(0f, -y);
            rowGo.GetComponent<Image>().color = new Color(30f / 255f, 26f / 255f, 22f / 255f, 0.95f);
            rowGo.GetComponent<Outline>().effectColor = new Color(160f / 255f, 130f / 255f, 90f / 255f, 1f);
            rowGo.GetComponent<Outline>().effectDistance = new Vector2(1.5f, -1.5f);

            var avGo = new GameObject("Avatar", typeof(RectTransform), typeof(Image));
            var av = (RectTransform)avGo.transform;
            av.SetParent(row, false);
            av.anchorMin = new Vector2(0f, 0.5f);
            av.anchorMax = new Vector2(0f, 0.5f);
            av.pivot = new Vector2(0f, 0.5f);
            av.sizeDelta = new Vector2(36f, 36f);
            av.anchoredPosition = new Vector2(6f, 0f);
            avGo.GetComponent<Image>().color = new Color(40f / 255f, 44f / 255f, 58f / 255f, 1f);
            var monoLabel = CreateLabel(av, Monogram(username, username), 16, Accent);
            Stretch(monoLabel.transform as RectTransform);
            monoLabel.alignment = TextAnchor.MiddleCenter;
            if (!string.IsNullOrWhiteSpace(pictureUrl))
                avGo.AddComponent<ImageDownloader>().Load(pictureUrl, av, ImageDownloader.FillMode.AspectFill, hideLabel: monoLabel.gameObject);

            var name = CreateLabel(row, username, 14, TextColor);
            SetRect((RectTransform)name.transform, new Vector2(-310f, 0f), new Vector2(400f, 20f));
            name.alignment = TextAnchor.MiddleLeft;

            CreateActionButton(row, "ACCEPT", new Vector2(320f, 0f), new Vector2(100f, 26f), onAccept);
            CreateActionButton(row, "DECLINE", new Vector2(210f, 0f), new Vector2(100f, 26f), onDecline);
            return row;
        }

        private static RectTransform CreateCard(RectTransform content, Vector2 center, Vector2 size, string heading)
        {
            var go = new GameObject("Card_" + heading, typeof(RectTransform), typeof(Image), typeof(Outline));
            var r = (RectTransform)go.transform;
            r.SetParent(content, false);
            r.anchorMin = new Vector2(0.5f, 1f);
            r.anchorMax = new Vector2(0.5f, 1f);
            r.pivot = new Vector2(0.5f, 1f);
            r.sizeDelta = size;
            r.anchoredPosition = new Vector2(center.x, center.y - size.y / 2f);
            go.GetComponent<Image>().color = new Color(24f / 255f, 27f / 255f, 36f / 255f, 0.97f);
            go.GetComponent<Outline>().effectColor = new Color(96f / 255f, 104f / 255f, 116f / 255f, 1f);
            go.GetComponent<Outline>().effectDistance = new Vector2(1.5f, -1.5f);

            var head = CreateLabel(r, heading, 12, SubText);
            SetRect((RectTransform)head.transform, new Vector2(-size.x / 2f + 14f, -22f), new Vector2(size.x - 28f, 16f));
            head.alignment = TextAnchor.MiddleLeft;

            var body = CreateLabel(r, "", 13, TextColor);
            SetRect((RectTransform)body.transform, new Vector2(-size.x / 2f + 14f, -44f), new Vector2(size.x - 28f, size.y - 52f));
            body.alignment = TextAnchor.UpperLeft;
            body.fontSize = 12;
            body.horizontalOverflow = HorizontalWrapMode.Wrap;
            body.verticalOverflow = VerticalWrapMode.Truncate;
            return r;
        }

        private static Button CreateTabButton(RectTransform parent, string label, float x, float y, float w, float h, Action onClick)
        {
            var go = new GameObject("Tab_" + label, typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            var r = (RectTransform)go.transform;
            r.SetParent(parent, false);
            r.anchorMin = new Vector2(0.5f, 0.5f);
            r.anchorMax = new Vector2(0.5f, 0.5f);
            r.pivot = new Vector2(0f, 0.5f);
            r.sizeDelta = new Vector2(w, h);
            r.anchoredPosition = new Vector2(x, y);
            var btn = go.GetComponent<Button>();
            btn.transition = Selectable.Transition.None;
            var t = CreateLabel(r, label, 13, TextColor);
            Stretch(t.transform as RectTransform);
            t.alignment = TextAnchor.MiddleCenter;
            btn.onClick.AddListener(() => onClick());
            return btn;
        }

        private static InputField CreateInputField(RectTransform content, string placeholder, Vector2 center, Vector2 size)
        {
            var go = new GameObject("Input_" + placeholder, typeof(RectTransform), typeof(Image), typeof(Outline), typeof(InputField));
            var r = (RectTransform)go.transform;
            r.SetParent(content, false);
            r.anchorMin = new Vector2(0.5f, 1f);
            r.anchorMax = new Vector2(0.5f, 1f);
            r.pivot = new Vector2(0.5f, 1f);
            r.sizeDelta = size;
            r.anchoredPosition = center;
            go.GetComponent<Image>().color = new Color(14f / 255f, 16f / 255f, 22f / 255f, 1f);
            go.GetComponent<Outline>().effectColor = new Color(96f / 255f, 104f / 255f, 116f / 255f, 1f);
            go.GetComponent<Outline>().effectDistance = new Vector2(1f, -1f);

            var input = go.GetComponent<InputField>();
            var txtGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
            var txtR = (RectTransform)txtGo.transform;
            txtR.SetParent(r, false);
            Stretch(txtR);
            txtR.offsetMin = new Vector2(8f, 2f);
            txtR.offsetMax = new Vector2(-8f, -2f);
            var txt = txtGo.GetComponent<Text>();
            txt.font = Font();
            txt.fontSize = 13;
            txt.color = TextColor;
            txt.alignment = TextAnchor.MiddleLeft;
            input.textComponent = txt;

            var phGo = new GameObject("Placeholder", typeof(RectTransform), typeof(Text));
            var phR = (RectTransform)phGo.transform;
            phR.SetParent(r, false);
            Stretch(phR);
            phR.offsetMin = new Vector2(8f, 2f);
            phR.offsetMax = new Vector2(-8f, -2f);
            var ph = phGo.GetComponent<Text>();
            ph.font = Font();
            ph.fontSize = 12;
            ph.fontStyle = FontStyle.Italic;
            ph.color = new Color(0.5f, 0.54f, 0.6f, 0.8f);
            ph.text = placeholder;
            ph.alignment = TextAnchor.MiddleLeft;
            input.placeholder = ph;
            return input;
        }

        private static Button CreateActionButton(RectTransform parent, string label, Vector2 center, Vector2 size, Action onClick)
        {
            // Buttons placed relative to top-left anchored content need top-left pivot.
            var go = new GameObject("ActionBtn_" + label, typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            var r = (RectTransform)go.transform;
            r.SetParent(parent, false);
            r.anchorMin = new Vector2(0.5f, 1f);
            r.anchorMax = new Vector2(0.5f, 1f);
            r.pivot = new Vector2(0.5f, 0.5f);
            r.sizeDelta = size;
            r.anchoredPosition = center;
            var img = go.GetComponent<Image>();
            img.color = ActionBg;
            go.GetComponent<Outline>().effectColor = ActionBorder;
            go.GetComponent<Outline>().effectDistance = new Vector2(1.5f, -1.5f);
            var btn = go.GetComponent<Button>();
            btn.transition = Selectable.Transition.None;
            var t = CreateLabel(r, label, 12, TextColor);
            Stretch(t.transform as RectTransform);
            t.alignment = TextAnchor.MiddleCenter;
            btn.onClick.AddListener(() => onClick());
            return btn;
        }

        private static Button CreateActionButton(RectTransform content, string label, Vector2 center, Vector2 size, Action onClick, bool _overloadMarker = true)
        {
            return CreateActionButton(content, label, center, size, onClick);
        }

        private static string Monogram(string username, string displayName)
        {
            var source = string.IsNullOrWhiteSpace(displayName) ? username : displayName;
            if (string.IsNullOrWhiteSpace(source)) return "?";
            return source.Substring(0, 1).ToUpperInvariant();
        }

        private static void OpenVeilnetProfile(string username)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://latticeveil.github.io/veilnet/profile/",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Open veilnet profile failed: " + ex.Message);
            }
        }

        private static void ClearPage(RectTransform content)
        {
            for (int i = content.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(content.GetChild(i).gameObject);
        }

        // ------------------------------------------------------------------
        // uGUI helpers
        // ------------------------------------------------------------------

        private static Text CreateLabel(RectTransform parent, string content, int size, Color color)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.font = Font();
            text.fontSize = size;
            text.color = color;
            text.alignment = TextAnchor.MiddleLeft;
            text.text = content;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            return text;
        }

        private static Font _fontCache;
        private static Font Font()
        {
            if (_fontCache == null)
                _fontCache = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return _fontCache;
        }

        private static void SetRect(RectTransform rect, Vector2 center, Vector2 size)
        {
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = center;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }

    /// <summary>
    /// Loads an image over HTTP with VeilnetClient's disk cache into an Image
    /// (aspect-fill or aspect-fit), optionally hiding a monogram label once
    /// loaded, and applies the bright-color rule (thumbnails stay vivid).
    /// </summary>
    public sealed class ImageDownloader : MonoBehaviour
    {
        public enum FillMode { AspectFill, AspectFit }

        private string _url;
        private RectTransform _target;
        private FillMode _mode;
        private GameObject _hideLabel;

        public void Load(string url, RectTransform target, FillMode mode, GameObject hideLabel = null)
        {
            _url = url;
            _target = target;
            _mode = mode;
        }

        private void Start()
        {
            StartCoroutine(LoadRoutine());
        }

        private IEnumerator LoadRoutine()
        {
            var done = false;
            Texture2D tex = null;

            if (VeilnetClient.TryGetCachedTexture(_url, out tex))
                done = true;
            if (!done)
            {
                yield return VeilnetClient.FetchImage(_url, t =>
                {
                    tex = t;
                    if (t != null) VeilnetClient.RememberTexture(_url, t);
                    done = true;
                });
            }
            while (!done) yield return null;

            if (tex == null || _target == null) yield break;
            var img = _target.GetComponent<Image>();
            if (img == null) yield break;

            img.color = Color.white;   // no dark placeholder multiply
            img.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 100f);

            if (_hideLabel != null)
                Destroy(_hideLabel);
        }
    }

    /// <summary>One controller instance per ProfileScreen.</summary>
    public sealed class ProfileScreenController : MonoBehaviour
    {
        public VeilnetClient.GateUser CurrentProfile = new VeilnetClient.GateUser { Username = "signed_out" };
        public List<VeilnetClient.GateUser> Friends = new List<VeilnetClient.GateUser>();
        public List<VeilnetClient.GateFriendRequestEntry> Incoming = new List<VeilnetClient.GateFriendRequestEntry>();
        public List<VeilnetClient.GateFriendRequestEntry> Outgoing = new List<VeilnetClient.GateFriendRequestEntry>();

        private RectTransform _root;
        private RectTransform _content;
        private Text _status;
        private readonly Button[] _tabs = new Button[3];
        private int _tab;
        private Action _onClosed;
        private bool _loading;

        public void Init(RectTransform root, RectTransform content, Text status)
        {
            _root = root;
            _content = content;
            _status = status;
            _onClosed = null;
            ScreenshotService.SetScreenContext("profile");
        }

        public void SetClosed(Action onClosed) { _onClosed = onClosed; }

        public void RegisterTab(int index, Button btn)
        {
            if (index >= 0 && index < 3)
                _tabs[index] = btn;
        }

        public void SelectTab(int index)
        {
            _tab = index;
            RestyleTabs();
            switch (index)
            {
                case 0: ProfileScreenBuilder.BuildIdentityPage(_content, this); break;
                case 1: ProfileScreenBuilder.BuildFriendsPage(_content, this); break;
                case 2: ProfileScreenBuilder.BuildRequestsPage(_content, this); break;
            }
            OptionsScreenBuilder.ResetScroll(_content.GetComponentInParent<ScrollRect>());
        }

        private void RestyleTabs()
        {
            for (int i = 0; i < _tabs.Length; i++)
            {
                if (_tabs[i] == null) continue;
                var img = _tabs[i].GetComponent<Image>();
                var line = _tabs[i].GetComponent<Outline>();
                var txt = _tabs[i].GetComponentInChildren<Text>();
                bool active = i == _tab;
                img.color = active ? new Color(52f / 255f, 58f / 255f, 68f / 255f, 1f) : new Color(24f / 255f, 28f / 255f, 34f / 255f, 0.9f);
                line.effectColor = active ? new Color(0.49f, 0.36f, 1f) : new Color(96f / 255f, 104f / 255f, 116f / 255f, 1f);
                txt.color = active ? Color.white : new Color(0.63f, 0.67f, 0.73f, 1f);
            }
        }

        public void Refresh()
        {
            if (_loading) return;
            _loading = true;
            StartCoroutine(LoadAll());
        }

        private IEnumerator LoadAll()
        {
            SetStatus(VeilnetClient.HasToken ? "SYNCING PROFILE…" : "NOT SIGNED IN — SHOWING LOCAL PLACEHOLDER");

            var profile = new VeilnetClient.ProfileResult();
            yield return VeilnetClient.FetchProfile(profile, () => { });

            if (profile.Ok)
                CurrentProfile = profile.Profile;
            else if (CurrentProfile == null)
            {
                CurrentProfile = new VeilnetClient.GateUser { Username = "signed_out" };
                if (VeilnetClient.HasToken)
                    SetStatus("Profile sync failed: " + profile.Error);
            }

            var friends = new VeilnetClient.FriendsResult();
            yield return VeilnetClient.FetchFriends(friends, () => { });

            Friends = friends.Friends;
            Incoming = friends.Incoming;
            Outgoing = friends.Outgoing;
            NotificationFeed.SyncFriendsInformations(Incoming);

            if (friends.Ok)
                SetStatus($"{Friends.Count} FRIENDS · {Incoming.Count} INCOMING · {Outgoing.Count} OUTGOING");
            else if (VeilnetClient.HasToken)
                SetStatus("Friends unavailable: " + friends.Error);

            _loading = false;
            SelectTab(_tab);
        }

        public void AddFriend(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                SetStatus("Enter a username or friend code first.");
                return;
            }
            StartCoroutine(AddFriendRoutine(query));
        }

        private IEnumerator AddFriendRoutine(string query)
        {
            SetStatus("SENDING FRIEND REQUEST…");
            var result = new VeilnetClient.MutationResult();
            yield return VeilnetClient.AddFriend(query, result, () => { });
            SetStatus(result.Ok ? "Friend request sent." : "Add failed: " + result.Error);
            if (result.Ok) Refresh();
        }

        public void RemoveFriend(string productUserId)
        {
            StartCoroutine(RemoveRoutine(productUserId));
        }

        private IEnumerator RemoveRoutine(string id)
        {
            var result = new VeilnetClient.MutationResult();
            yield return VeilnetClient.RemoveFriend(id, result, () => { });
            SetStatus(result.Ok ? "Friend removed." : "Remove failed: " + result.Error);
            if (result.Ok) Refresh();
        }

        public void RespondRequest(string requesterId, bool accept)
        {
            StartCoroutine(RespondRoutine(requesterId, accept));
        }

        private IEnumerator RespondRoutine(string requesterId, bool accept)
        {
            var result = new VeilnetClient.MutationResult();
            yield return VeilnetClient.RespondToFriendRequest(requesterId, accept, false, result, () => { });
            SetStatus(result.Ok ? (accept ? "Friend added." : "Request declined.") : "Respond failed: " + result.Error);
            if (result.Ok) Refresh();
        }

        private void SetStatus(string msg)
        {
            if (_status != null) _status.text = msg;
        }

        public void Close()
        {
            _onClosed?.Invoke();
            Destroy(gameObject);
        }

        private float _openedAt;

        private void Update()
        {
            // Swallow the same-frame ESC that opened us (menu keyup leak),
            // so the profile never insta-closes on the opening frame.
            if (_openedAt <= 0f) _openedAt = Time.unscaledTime;
            if (Input.GetKeyDown(KeyCode.Escape) && Time.unscaledTime - _openedAt > 0.1f)
                Close();
        }
    }
}
