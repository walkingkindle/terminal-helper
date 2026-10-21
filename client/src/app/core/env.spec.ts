import { envLabel, EnvSettings, sanitizeEnv, toApiEnv, withOs } from './env';

const arch: EnvSettings = { os: 'linux', distro: 'Arch Linux', shell: 'bash', notes: ' yay for AUR ' };

describe('env', () => {
  it('labels each kind of setup', () => {
    expect(envLabel(arch)).toBe('Arch Linux · bash');
    expect(envLabel({ ...arch, distro: '' })).toBe('Linux · bash');
    expect(envLabel({ os: 'windows', distro: '', shell: 'Git Bash', notes: '' })).toBe('Windows · Git Bash');
    expect(envLabel({ os: 'windows', distro: '', shell: 'WSL', notes: '' })).toBe('Windows · WSL Ubuntu');
    expect(envLabel({ os: 'macos', distro: 'Arch Linux', shell: 'zsh', notes: '' })).toBe('macOS · zsh');
  });

  it('only sends a distro where one applies', () => {
    expect(toApiEnv(arch)).toEqual({ os: 'Linux', distro: 'Arch Linux', shell: 'bash', notes: 'yay for AUR' });
    expect(toApiEnv({ ...arch, os: 'windows', shell: 'PowerShell' }).distro).toBe('');
    expect(toApiEnv({ ...arch, os: 'windows', shell: 'WSL', distro: '' }).distro).toBe('Ubuntu');
  });

  it('keeps the shell across OS switches only when it exists there', () => {
    expect(withOs(arch, 'macos').shell).toBe('bash');
    expect(withOs(arch, 'windows').shell).toBe('PowerShell');
    expect(withOs({ ...arch, shell: 'Git Bash', os: 'windows' }, 'linux').shell).toBe('bash');
  });

  it('repairs whatever was stored', () => {
    expect(sanitizeEnv({ os: 'linux', shell: 'cmd', distro: 42 })).toMatchObject({
      os: 'linux',
      shell: 'bash',
      distro: '',
    });
    expect(sanitizeEnv('nonsense').os).toMatch(/linux|macos|windows/);
  });
});
