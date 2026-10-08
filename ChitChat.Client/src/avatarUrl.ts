export function avatarUrl(userName: string, version?: number): string {
	const base = `${import.meta.env.BASE_URL}api/avatar/${encodeURIComponent(userName)}`;
	return version ? `${base}?v=${version}` : base;
}
