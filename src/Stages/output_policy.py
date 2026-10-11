"""Prospective D58 delivery boundary; frozen packages are never edited."""
def successor_members(data,variant,plugin):
    removed=[n for n in data if n.lower().startswith('obodyng_pda_')]
    for name in removed:del data[name]
    data['README-T08.txt']=(
        f'Corrected follower package: {variant}\n'
        f'Use exactly one complete variant and enable {plugin}. Preserve original saves and prior packages.\n'
        'Sen Abersha retains the captured Understone Keep placement, FaceGen, race applicability and follower setup.\n'
        'Prebuilt: XML-built actual OBody targets; unaffected assets use captured geometry as-is. Intended for hosts without OBody.\n'
        'OBody: actual captured winning installed body AND outfit meshes with compatible captured morph files, private-reference edits only. Apply the packaged preset using compatible installed OBody facilities.\n'
        'The actual authored preset is preserved. No PDA, custom DLL/PEX, automatic distribution addon or global configuration change is required or included.\n'
        'Installed OBody selected-actor reset is source-checked. Existing-save switching, random assignment protection on an OBody-enabled prebuilt host and persistence remain unverified; no automatic migration is claimed.\n'
        'Called worn/first-person/ground models and effective texture/material dependencies must pass the package validator. Original meshes, source mods and manual followers are not modified.\n'
        'Compatible host physics and Creation Club acquisition behavior remain dependencies. Offline structure/path validation does not establish game behavior.\n'
    ).encode()
    return removed
