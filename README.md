# Discord Voice Translator

**[English](#english) · [Français](#français)**

---

## English

Live subtitles for your Discord voice chat, translated into your language and shown in an
overlay on top of your games, with the name and avatar of whoever is talking. You can also
add a game with voice chat (CS2, War Dogs...) or any other app that plays voices.

- Hears **only the apps you choose** (Discord by default): not the rest of the PC, not your own microphone.
- Speech recognition runs **on your PC** (OpenAI Whisper, on your graphics card).
- Translation by **DeepL** (free plan: 500,000 characters per month).
- **No bot and no modified Discord client.** Names come from your own Discord app through its official local interface.

### Requirements

- **Windows 11** (64-bit). Windows 10 cannot capture the sound of a single app.
- The **Discord desktop app** for names and avatars (the browser version does not work).
- A **graphics card** is recommended (NVIDIA or AMD, about 1 GB of video memory). It also runs on the processor, but much slower.
- About **1 GB of free space**, and internet on the first launch (the default speech model is 834 MB).

### Install

1. Download **`DiscordVoiceTranslator-Setup-x.y.z.exe`** from the [latest release](../../releases/latest).
2. Run it. Windows may show **"Windows protected your PC"**: the app is not code-signed (a certificate costs money). Click **More info**, then **Run anyway**.
3. Click through the installer (no admin rights needed). The app starts at the end.
4. On the first launch, the app downloads the speech model (progress is shown in the overlay) and opens its **Settings**.

**Updating:** download the new installer and run it, no need to uninstall first. It closes the app if it is open, installs over the old version and keeps your settings, API keys and downloaded speech models.

### Setup (5 minutes, once)

**Show who is talking (Discord tab in Settings)**

1. Open the [Discord Developer Portal](https://discord.com/developers/applications) and click **New Application** (any name).
2. Open the **OAuth2** page: copy the **Client ID**, then click **Reset Secret** and copy the **Client Secret**.
3. On the same page, under **Redirects**, add `http://localhost` and click **Save Changes**.
4. Paste both values in the **Discord** tab and click **Save**. Discord asks you to authorize the app: click **Authorize**. This happens only once.

Without this step, subtitles still work, but each line shows the language instead of a name.

**Translation (DeepL tab in Settings)**

1. Get a free key at [deepl.com/pro-api](https://www.deepl.com/pro-api) (choose **DeepL API Free**). Your key is on your account page and ends with `:fx`.
2. Paste it in the **DeepL** tab, click **Add**, then **Save**.
   You can add several keys: they are used from top to bottom, and when the monthly quota of one runs out, the next one takes over automatically.
3. Choose the language to translate into in the **General** tab (English by default).

### Everyday use

- **Ctrl+Alt+F9**: edit mode (drag to move, pull the edges or corners to resize), press again to lock. The overlay snaps to the screen edges and center: hold **Shift** to place it freely, **Ctrl** to resize from the center, **Esc** to cancel. Once locked, your clicks go through to the game.
- **Ctrl+Alt+F10**: hide or show the subtitles.
- **Ctrl+Alt+F11**: pause or resume (also in the tray menu and the edit-mode bar). While paused nothing is listened to or translated, so no DeepL characters are used, and the app stays open.
- **Ctrl+Alt+H**: history of the last lines, to reread what went by too fast (copy, export to a text file, or hide a person: a music bot, a friend who already speaks your language).
- **Ctrl+Alt+R**: reply in their language. Type in yours, press Enter: the translation is copied, paste it in Discord with Ctrl+V.
- **Live subtitles** (Settings → Overlay, on by default): on long sentences (over 2–3 seconds), the text appears and is translated while the person is still speaking, then is corrected when they finish. Uses more DeepL characters: turn it off to save quota.
- **Names and game vocabulary** (Settings → General, on by default): the names of the people in your voice channel and the terms of the game that is running are spelled correctly and never translated. Lists for 52 games ship with the app ([see them](src/DiscordVoiceTranslator.App/vocabulary)): Apex Legends, ARC Raiders, Arma, Baldur's Gate 3, Battlefield, Call of Duty, Counter-Strike 2, Cyberpunk 2077, DARK SOULS III, Dead by Daylight, Deadlock, Destiny 2, Dota 2, ELDEN RING, Escape from Tarkov, FINAL FANTASY XIV Online, Fortnite, Forza, Grand Theft Auto, HELLDIVERS 2, Hollow Knight, Hollow Knight: Silksong, League of Legends, Marvel Rivals, Minecraft, Monster Hunter Wilds, osu!, Overwatch, Palworld, Path of Exile, PEAK, Project Zomboid, PUBG: BATTLEGROUNDS, R.E.P.O., Roblox, Rocket League, Rust, Slay the Spire 2, Squad, Stardew Valley, Team Fortress 2, Terraria, The Last of Us, Tom Clancy's Rainbow Six Siege, Trackmania, Valheim, VALORANT, VRChat, War Thunder, WARDOGS, Warframe, World of Warcraft. To add a game for yourself, put a JSON file in the same format in `%APPDATA%\DiscordVoiceTranslator\vocabulary`; to share it, propose it on GitHub.
- **Casual tone** (Settings → General): translates with "tu" rather than "vous" (and the like) between players.
- The app lives in the **notification area**: click the **^** arrow next to the clock, then right-click the blue speech bubble for **Settings** or **Quit**.
- Works with games in **borderless windowed** mode (not exclusive fullscreen).
- The interface is in English or French: **Settings → General → Interface language**.
- **Settings → Audio**: subtitle Discord, and add games or other apps that are open (launch the game first so it appears in the list). Their lines are labelled with the app's name and the language. The whole sound of a game is listened to, so songs with lyrics can give false lines.
- **Settings → Overlay → Subtitle theme**: *Classic* (dark box), *Cinema* (outlined text, no box), *Chat* (Discord-style message) or *Neon* (glowing border in the speaker's color), with a preview of each.
- **Settings → Transcription → Speech model**: *Fast* (Whisper large-v3-turbo, the default, light on the PC) or *Accurate* (Whisper large-v3: fewer mistakes, but slower and heavier on the graphics card, about 2 GB of video memory). A new model is downloaded once, at the next start.
- **Settings → General → Status** shows what the app is doing: graphics card used, memory, delay, DeepL quota, Discord connection.

### Privacy

- No audio is ever recorded or sent anywhere: speech recognition happens on your PC.
- The subtitle history (Ctrl+Alt+H) stays in memory only and is gone when the app closes, unless you export it.
- When translation is on, only the recognized **text** is sent to DeepL.
- At startup the app asks GitHub for the latest version number, to tell you when an update is out (Settings → General to turn it off).
- Your DeepL key and Discord application are stored **encrypted with your Windows account** in `%APPDATA%\DiscordVoiceTranslator`.

### Troubleshooting

| Problem | What to do |
|---|---|
| No subtitles at all | The Discord **desktop** app must be running and you must be in a voice channel. Check **Settings → General → Status**. |
| Lines show a language instead of a name | Do the Discord setup above. If it is done, the Status line starting with "Names:" tells what is wrong (often the missing `http://localhost` redirect). |
| Lines often show no name | Raise **Name matching tolerance** in the Discord tab. |
| Overlay not visible in game | Switch the game to **borderless windowed**. |
| Sentences cut in two | Raise **Pause that ends a sentence** in the Transcription tab. |
| Hotkey marked "ALREADY IN USE" | Another program uses that shortcut. |

### Uninstall

**Windows Settings → Apps → Installed apps → Discord Voice Translator → Uninstall.** This removes
the app and the speech models. At the end it asks whether to also delete your settings and encrypted
keys (`%APPDATA%\DiscordVoiceTranslator`); keep them if you plan to reinstall.

### Build from source

Requires the .NET 8 SDK and [Inno Setup 6](https://jrsoftware.org/isinfo.php)
(`winget install JRSoftware.InnoSetup`), then:

```powershell
powershell -ExecutionPolicy Bypass -File build\release.ps1 -Version 1.6.0
```

The installer is written to `artifacts\`. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
for the components used. Not affiliated with Discord, DeepL or OpenAI.

---

## Français

Des sous-titres en direct pour ton vocal Discord, traduits dans ta langue et affichés dans un
overlay par-dessus tes jeux, avec le pseudo et l'avatar de la personne qui parle. Tu peux aussi
ajouter un jeu avec un chat vocal (CS2, War Dogs...) ou n'importe quelle autre appli qui diffuse des voix.

- Capte **uniquement les applis que tu choisis** (Discord par défaut) : ni le reste du PC, ni ton propre micro.
- La reconnaissance vocale tourne **sur ton PC** (OpenAI Whisper, sur ta carte graphique).
- Traduction par **DeepL** (offre gratuite : 500 000 caractères par mois).
- **Aucun bot, aucun client Discord modifié.** Les pseudos viennent de ta propre application Discord, via son interface locale officielle.

### Configuration requise

- **Windows 11** (64 bits). Windows 10 ne sait pas capter le son d'un seul programme.
- L'**application Discord pour PC** pour les pseudos et avatars (la version navigateur ne marche pas).
- Une **carte graphique** est recommandée (NVIDIA ou AMD, environ 1 Go de mémoire vidéo). L'appli tourne aussi sur le processeur, mais bien plus lentement.
- Environ **1 Go d'espace libre**, et internet au premier lancement (le modèle de reconnaissance par défaut fait 834 Mo).

### Installation

1. Télécharge **`DiscordVoiceTranslator-Setup-x.y.z.exe`** depuis la [dernière version](../../releases/latest).
2. Lance-le. Windows peut afficher **« Windows a protégé votre ordinateur »** : l'appli n'est pas signée (un certificat est payant). Clique sur **Informations complémentaires**, puis **Exécuter quand même**.
3. Suis l'installateur (pas besoin de droits administrateur). L'appli démarre à la fin.
4. Au premier lancement, l'appli télécharge le modèle de reconnaissance (la progression s'affiche dans l'overlay) et ouvre ses **paramètres**.

**Mise à jour :** télécharge le nouvel installateur et lance-le, sans désinstaller avant. Il ferme l'appli si elle est ouverte, s'installe par-dessus l'ancienne version et garde tes réglages, clés API et modèles de reconnaissance téléchargés.

### Réglages (5 minutes, une seule fois)

L'interface est en anglais par défaut : passe-la en français dans **Settings → General → Interface language → Français**, puis redémarre quand l'appli le propose.

**Afficher qui parle (onglet Discord des paramètres)**

1. Ouvre le [portail développeur Discord](https://discord.com/developers/applications) et clique sur **New Application** (n'importe quel nom).
2. Ouvre la page **OAuth2** : copie le **Client ID**, puis clique sur **Reset Secret** et copie le **Client Secret**.
3. Sur la même page, dans **Redirects**, ajoute `http://localhost` et clique sur **Save Changes**.
4. Colle les deux valeurs dans l'onglet **Discord** et enregistre. Discord te demande d'autoriser l'appli : clique sur **Autoriser**. Ça n'arrive qu'une fois.

Sans cette étape, les sous-titres marchent quand même, mais chaque ligne affiche la langue au lieu d'un pseudo.

**Traduction (onglet DeepL des paramètres)**

1. Crée une clé gratuite sur [deepl.com/pro-api](https://www.deepl.com/pro-api) (choisis **DeepL API Free**). Ta clé est sur la page de ton compte et finit par `:fx`.
2. Colle-la dans l'onglet **DeepL**, clique sur **Ajouter**, puis enregistre.
   Tu peux ajouter plusieurs clés : elles servent de haut en bas, et quand le quota du mois de l'une est épuisé, la suivante prend le relais automatiquement.
3. Choisis la langue de traduction dans l'onglet **Général** (anglais par défaut).

### Au quotidien

- **Ctrl+Alt+F9** : mode édition (glisser pour déplacer, tirer les bords ou les coins pour redimensionner), puis de nouveau pour verrouiller. L'overlay s'aimante aux bords et au centre de l'écran : **Maj** enfoncée pour le placer librement, **Ctrl** pour redimensionner depuis le centre, **Échap** pour annuler. Une fois verrouillé, tes clics passent au jeu.
- **Ctrl+Alt+F10** : masquer ou afficher les sous-titres.
- **Ctrl+Alt+F11** : mettre en pause ou reprendre (aussi dans le menu de l'icône et la barre du mode édition). En pause, rien n'est écouté ni traduit, donc aucun caractère DeepL n'est consommé, et l'appli reste ouverte.
- **Ctrl+Alt+H** : historique des dernières lignes, pour relire ce qui est passé trop vite (copier, exporter en fichier texte, ou masquer une personne : un bot musique, un ami qui parle déjà ta langue).
- **Ctrl+Alt+R** : répondre dans leur langue. Tape dans la tienne, appuie sur Entrée : la traduction est copiée, colle-la dans Discord avec Ctrl+V.
- **Sous-titres en direct** (Paramètres → Overlay, activés par défaut) : sur les phrases longues (plus de 2 à 3 secondes), le texte s'affiche et se traduit pendant que la personne parle encore, puis se corrige quand elle a fini. Consomme plus de caractères DeepL : désactive-les pour économiser le quota.
- **Pseudos et vocabulaire du jeu** (Paramètres → Général, activé par défaut) : les pseudos des gens de ton salon vocal et les termes du jeu en cours sont écrits correctement et jamais traduits. Des listes pour 52 jeux sont fournies avec l'appli ([les voir](src/DiscordVoiceTranslator.App/vocabulary)) : Apex Legends, ARC Raiders, Arma, Baldur's Gate 3, Battlefield, Call of Duty, Counter-Strike 2, Cyberpunk 2077, DARK SOULS III, Dead by Daylight, Deadlock, Destiny 2, Dota 2, ELDEN RING, Escape from Tarkov, FINAL FANTASY XIV Online, Fortnite, Forza, Grand Theft Auto, HELLDIVERS 2, Hollow Knight, Hollow Knight: Silksong, League of Legends, Marvel Rivals, Minecraft, Monster Hunter Wilds, osu!, Overwatch, Palworld, Path of Exile, PEAK, Project Zomboid, PUBG: BATTLEGROUNDS, R.E.P.O., Roblox, Rocket League, Rust, Slay the Spire 2, Squad, Stardew Valley, Team Fortress 2, Terraria, The Last of Us, Tom Clancy's Rainbow Six Siege, Trackmania, Valheim, VALORANT, VRChat, War Thunder, WARDOGS, Warframe, World of Warcraft. Pour ajouter un jeu pour toi, dépose un fichier JSON au même format dans `%APPDATA%\DiscordVoiceTranslator\vocabulary` ; pour le partager, propose-le sur GitHub.
- **Ton familier** (Paramètres → Général) : traduit avec « tu » plutôt que « vous » entre joueurs.
- L'appli vit dans la **zone de notification** : clique sur la flèche **^** près de l'horloge, puis clic droit sur la bulle bleue pour les **Paramètres** ou **Quitter**.
- Marche avec les jeux en **fenêtré sans bordure** (pas en plein écran exclusif).
- **Paramètres → Audio** : sous-titrer Discord, et ajouter des jeux ou d'autres applis ouvertes (lance d'abord le jeu pour qu'il apparaisse dans la liste). Leurs lignes portent le nom de l'appli et la langue. Tout le son d'un jeu est écouté : des chansons avec paroles peuvent donner de fausses lignes.
- **Paramètres → Overlay → Thème des sous-titres** : *Classique* (boîte sombre), *Cinéma* (texte cerné, sans boîte), *Discussion* (message façon Discord) ou *Néon* (contour lumineux à la couleur de la personne), avec un aperçu de chacun.
- **Paramètres → Transcription → Modèle de reconnaissance** : *Rapide* (Whisper large-v3-turbo, par défaut, léger pour le PC) ou *Précis* (Whisper large-v3 : moins d'erreurs, mais plus lent et plus lourd pour la carte graphique, environ 2 Go de mémoire vidéo). Un nouveau modèle est téléchargé une seule fois, au prochain démarrage.
- **Paramètres → Général → État** montre ce que fait l'appli : carte graphique utilisée, mémoire, délai, quota DeepL, connexion Discord.

### Confidentialité

- Aucun son n'est enregistré ni envoyé nulle part : la reconnaissance vocale se fait sur ton PC.
- L'historique des sous-titres (Ctrl+Alt+H) reste en mémoire et disparaît à la fermeture de l'appli, sauf si tu l'exportes.
- Quand la traduction est active, seul le **texte** reconnu est envoyé à DeepL.
- Au démarrage, l'appli demande à GitHub le numéro de la dernière version, pour te prévenir d'une mise à jour (désactivable dans Paramètres → Général).
- Ta clé DeepL et ton application Discord sont enregistrées **chiffrées avec ton compte Windows** dans `%APPDATA%\DiscordVoiceTranslator`.

### Dépannage

| Problème | Que faire |
|---|---|
| Aucun sous-titre | L'application Discord **pour PC** doit être lancée et tu dois être dans un salon vocal. Regarde **Paramètres → Général → État**. |
| Les lignes affichent une langue au lieu d'un pseudo | Fais la configuration Discord ci-dessus. Si c'est fait, la ligne d'état « Pseudos : » dit ce qui ne va pas (souvent la redirection `http://localhost` oubliée). |
| Les lignes s'affichent souvent sans pseudo | Augmente **Tolérance pour attribuer les pseudos** dans l'onglet Discord. |
| Overlay invisible en jeu | Passe le jeu en **fenêtré sans bordure**. |
| Phrases coupées en deux | Augmente **Pause qui termine une phrase** dans l'onglet Transcription. |
| Raccourci marqué « DÉJÀ PRIS » | Un autre programme utilise ce raccourci. |

### Désinstallation

**Paramètres Windows → Applications → Applications installées → Discord Voice Translator → Désinstaller.**
Cela retire l'appli et les modèles de reconnaissance. À la fin, il te demande s'il faut aussi effacer tes réglages
et clés chiffrées (`%APPDATA%\DiscordVoiceTranslator`) ; garde-les si tu comptes réinstaller.

### Compiler depuis les sources

Il faut le SDK .NET 8 et [Inno Setup 6](https://jrsoftware.org/isinfo.php)
(`winget install JRSoftware.InnoSetup`), puis :

```powershell
powershell -ExecutionPolicy Bypass -File build\release.ps1 -Version 1.6.0
```

L'installateur est écrit dans `artifacts\`. Voir [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
pour les composants utilisés. Projet sans lien avec Discord, DeepL ou OpenAI.
