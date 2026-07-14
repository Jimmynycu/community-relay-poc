import { readFile } from 'node:fs/promises';
import { parseAppConfig } from '@devvit/shared-types/schemas/config-file.v1.js';

const source = await readFile(new URL('../devvit.json', import.meta.url), 'utf8');
const config = parseAppConfig(source, false);

console.log(`Devvit schema valid: ${config.name}`);
