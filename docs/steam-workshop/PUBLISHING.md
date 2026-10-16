# Steam Workshop publishing

Workshop item: **3821032491** (https://steamcommunity.com/sharedfiles/filedetails/?id=3821032491).
GitHub releases are the source of truth; the Workshop mirrors them.

The item's title comes from `SubModule.xml` (`Modular Warehouses + (Coop)`). The tags, image and visibility come from
the XML task files here, and the full page text from `description.bbcode.txt` (paste it into the page with
"Owner controls -> Edit title & description").

## Uploading
1. Bump `<Version>` in `src/CoopWarehouses/_Module/SubModule.xml`. Coop requires the server and clients to match, so
   never push an update without a version bump.
2. Build (`dotnet build -c Release`) and stage a clean module in `artifacts/CoopWarehouses` (git ignores it). It should
   contain only `SubModule.xml`, `preview.jpg` (from this folder, under 1 MB) and `bin\Win64_Shipping_Client` plus
   `bin\Win64_Shipping_Server` with the two DLLs each. No .pdb files, no sources, no logs.
3. Put the release notes in `WorkshopUpdate.xml`, then, with Steam running:

   ```powershell
   cd 'D:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\bin\Win64_Shipping_Client'
   .\TaleWorlds.MountAndBlade.SteamWorkshop.exe 'D:\Design\Bannerlord Mods\CoopWarehouses\docs\steam-workshop\WorkshopUpdate.xml'
   ```

The uploader always ends with a `Console.ReadKey` crash. That is not a failure: look for `Uploading done!`, and
confirm in `D:\Program Files (x86)\Steam\logs\workshop_log.txt` ("Upload finished for workshop item 3821032491 : OK").
While the item is Private, the public `GetPublishedFileDetails` API answers result 9 (not found).

## Dedicated server
The Coop dedicated server does not update from the Workshop. Replace its
`...\3770450698\DedicatedServer\engine\Modules\CoopWarehouses` with the new build (or a junction to the subscribed
Workshop folder) and restart it after each push.
