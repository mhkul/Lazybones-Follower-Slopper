"""Names carried by the typed settings plan; legacy plans keep their GUID route."""
import re

def level_summary(settings):
    values = (settings or {}).get('values')
    if not values:
        return ''
    if values['levelMode'] == 'fixed':
        return 'Fixed follower level: ' + str(values['minimumLevel']) + '.'
    cap = values.get('maximumLevel')
    maximum = 'no user-set cap (CalcMaxLevel 0)' if cap is None else str(cap)
    return ('Player-relative follower level: ' + str(values['levelMultiplier']) +
            ' x player level; minimum ' + str(values['minimumLevel']) +
            '; maximum ' + maximum + '.')

def require_settings_readback(plan, validation):
    expected=plan.get('followerSettings')
    if expected and validation.get('settings',{}).get('values') != expected['values']:
        raise ValueError('Compiled PluginWriter lacks matching follower-settings readback; rebuild or select the current writer')

def names(plan, owner):
    identity = owner['buildId'].replace('-', '')
    settings = plan.get('followerSettings')
    if settings and settings.get('pluginName'):
        name = settings['values']['modName']
        if not isinstance(name,str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9 _-]{0,63}',name) or name.endswith(' '):
            raise ValueError('Invalid resolved mod name; refresh follower settings')
        if settings['pluginName'] != name + '.esp' or settings['assetNamespace'] != 'autofollower/' + name.lower():
            raise ValueError('Named plugin/asset namespace differs from settings')
        return dict(plugin=name+'.esp', namespace=settings['assetNamespace'], identity=name.lower(), display=name)
    return dict(plugin='AutoFollower_'+identity+'.esp', namespace='autofollower/'+identity, identity=identity, display=None)
