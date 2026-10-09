# A partial vector run may extend a snapshot, but cannot change any input already used by saved vectors.
def differences(old, new):
    changed = []
    for key, table in old['tables'].items():
        candidate = new['tables'].get(key)
        if candidate is None or any(v != candidate.get(k) for k, v in table.items() if k != 'extra'):
            changed.append(f'table {key}')
            continue
        if any(v != candidate.get('extra', {}).get(i, {}).get(k)
               for i, fields in table.get('extra', {}).items() for k, v in fields.items()):
            changed.append(f'table {key} vectors')
    for key in ('byid', 'worldstate', 'settings', 'cmdactions'):
        if old.get(key) != new.get(key): changed.append(key)
    for group in ('flags', 'lists'):
        for key, value in old[group].items():
            if value != new[group].get(key): changed.append(f'{group} {key}')
    return changed
