# Discord Voice Translator

**[English](#english) · [Français](#français)**

---

## English

Live subtitles for your Discord voice chat, translated into your language and shown in an
overlay on top of your games, with the name and avatar of whoever is talking.

- Hears **only Discord**: not your game, not your music, not your own microphone.
- Speech recognition runs **on your PC** (OpenAI Whisper, on your graphics card).
- Translation by **DeepL** (free plan: 500,000 characters per month).
- **No bot and no modified Discord client.** Names come from your own Discord app through its official local interface.

### Requirements

- **Windows 11** (64-bit). Windows 10 cannot capture the sound of a single app.
- The **Discord desktop app** (the browser version does not work).
- A **graphics card** is recommended (NVIDIA or AMD, about 1 GB of video memory). It also runs on the processor, but much slower.
- About **1 GB of free space**, and internet on the first launch (the default speech model is 834 MB).

### Install

1. Download **`DiscordVoiceTranslator-Setup-x.y.z.exe`** from the [latest release](../../releases/latest).
2. Run it. Windows may show **"Windows protected your PC"**: the app is not code-signed (a certificate costs money). Click **More info**, then **Run anyway**.
3. Click through the installer (no admin rights needed). The app starts at the end.
4. On the first launch, the app downloads the speech model (progress is shown in the overlay) and opens its **Settings**.

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

- **Ctrl+Alt+F9**: edit mode (drag to move, bottom-right corner to resize), press again to lock. Once locked, your clicks go through to the game.
- **Ctrl+Alt+F10**: hide or show the subtitles.
- The app lives in the **notification area**: click the **^** arrow next to the clock, then right-click the blue speech bubble for **Settings** or **Quit**.
- Works with games in **borderless windowed** mode (not exclusive fullscreen).
- The interface is in English or French: **Settings → General → Interface language**.
- **Settings → Transcription → Speech model**: *Fast* (Whisper large-v3-turbo, the default, light on the PC) or *Accurate* (Whisper large-v3: fewer mistakes, but slower and heavier on the graphics card, about 2 GB of video memory). A new model is downloaded once, at the next start.
- **Settings → General → Status** shows what the app is doing: graphics card used, memory, delay, DeepL quota, Discord connection.

### Privacy

- No audio is ever recorded or sent anywhere: speech recognition happens on your PC.
- When translation is on, only the recognized **text** is sent to DeepL.
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
the app and the speech model. Your settings and encrypted keys stay in
`%APPDATA%\DiscordVoiceTranslator`; delete that folder to remove them too.

### Build from source

Requires the .NET 8 SDK and [Inno Setup 6](https://jrsoftware.org/isinfo.php)
(`winget install JRSoftware.InnoSetup`), then:

```powershell
powershell -ExecutionPolicy Bypass -File build\release.ps1 -Version 1.2.1
```

The installer is written to `artifacts\`. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
for the components used. Not affiliated with Discord, DeepL or OpenAI.

---

## Français

Des sous-titres en direct pour ton vocal Discord, traduits dans ta langue et affichés dans un
overlay par-dessus tes jeux, avec le pseudo et l'avatar de la personne qui parle.

- Capte **uniquement Discord** : ni ton jeu, ni ta musique, ni ton propre micro.
- La reconnaissance vocale tourne **sur ton PC** (OpenAI Whisper, sur ta carte graphique).
- Traduction par **DeepL** (offre gratuite : 500 000 caractères par mois).
- **Aucun bot, aucun client Discord modifié.** Les pseudos viennent de ta propre application Discord, via son interface locale officielle.

### Configuration requise

- **Windows 11** (64 bits). Windows 10 ne sait pas capter le son d'un seul programme.
- L'**application Discord pour PC** (la version navigateur ne marche pas).
- Une **carte graphique** est recommandée (NVIDIA ou AMD, environ 1 Go de mémoire vidéo). L'appli tourne aussi sur le processeur, mais bien plus lentement.
- Environ **1 Go d'espace libre**, et internet au premier lancement (le modèle de reconnaissance par défaut fait 834 Mo).

### Installation

1. Télécharge **`DiscordVoiceTranslator-Setup-x.y.z.exe`** depuis la [dernière version](../../releases/latest).
2. Lance-le. Windows peut afficher **« Windows a protégé votre ordinateur »** : l'appli n'est pas signée (un certificat est payant). Clique sur **Informations complémentaires**, puis **Exécuter quand même**.
3. Suis l'installateur (pas besoin de droits administrateur). L'appli démarre à la fin.
4. Au premier lancement, l'appli télécharge le modèle de reconnaissance (la progression s'affiche dans l'overlay) et ouvre ses **paramètres**.

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

- **Ctrl+Alt+F9** : mode édition (glisser pour déplacer, coin bas-droit pour redimensionner), puis de nouveau pour verrouiller. Une fois verrouillé, tes clics passent au jeu.
- **Ctrl+Alt+F10** : masquer ou afficher les sous-titres.
- L'appli vit dans la **zone de notification** : clique sur la flèche **^** près de l'horloge, puis clic droit sur la bulle bleue pour les **Paramètres** ou **Quitter**.
- Marche avec les jeux en **fenêtré sans bordure** (pas en plein écran exclusif).
- **Paramètres → Transcription → Modèle de reconnaissance** : *Rapide* (Whisper large-v3-turbo, par défaut, léger pour le PC) ou *Précis* (Whisper large-v3 : moins d'erreurs, mais plus lent et plus lourd pour la carte graphique, environ 2 Go de mémoire vidéo). Un nouveau modèle est téléchargé une seule fois, au prochain démarrage.
- **Paramètres → Général → État** montre ce que fait l'appli : carte graphique utilisée, mémoire, délai, quota DeepL, connexion Discord.

### Confidentialité

- Aucun son n'est enregistré ni envoyé nulle part : la reconnaissance vocale se fait sur ton PC.
- Quand la traduction est active, seul le **texte** reconnu est envoyé à DeepL.
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
Cela retire l'appli et le modèle de reconnaissance. Tes réglages et clés chiffrées restent dans
`%APPDATA%\DiscordVoiceTranslator` ; supprime ce dossier pour les effacer aussi.

### Compiler depuis les sources

Il faut le SDK .NET 8 et [Inno Setup 6](https://jrsoftware.org/isinfo.php)
(`winget install JRSoftware.InnoSetup`), puis :

```powershell
powershell -ExecutionPolicy Bypass -File build\release.ps1 -Version 1.2.1
```

L'installateur est écrit dans `artifacts\`. Voir [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
pour les composants utilisés. Projet sans lien avec Discord, DeepL ou OpenAI.
