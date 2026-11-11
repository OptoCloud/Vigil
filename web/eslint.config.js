// Lint policy for vigil-web. docs/STYLE.md §9 is the prose version; the rules below are the
// subset a linter can actually check. Prettier owns formatting, so no stylistic rules here.

import js from '@eslint/js';
import prettier from 'eslint-config-prettier';
import { defineConfig } from 'eslint/config';
import svelte from 'eslint-plugin-svelte';
import globals from 'globals';
import ts from 'typescript-eslint';

// defineConfig rather than typescript-eslint's ts.config, which it deprecates in its favour.
export default defineConfig(
	js.configs.recommended,
	...ts.configs.recommended,
	...svelte.configs.recommended,
	// Prettier owns formatting: switch off every core and plugin rule that would argue with it.
	prettier,
	...svelte.configs.prettier,
	{
		languageOptions: { globals: { ...globals.browser, ...globals.node } }
	},
	{
		files: ['**/*.svelte', '**/*.svelte.ts'],
		languageOptions: { parserOptions: { parser: ts.parser } }
	},
	{
		rules: {
			// STYLE.md §9: no `any`, and no non-null assertion without a stated reason. An
			// eslint-disable line with a justification IS that stated reason.
			'@typescript-eslint/no-explicit-any': 'error',
			'@typescript-eslint/no-non-null-assertion': 'error',
			'@typescript-eslint/consistent-type-imports': ['error', { prefer: 'type-imports' }],
			eqeqeq: ['error', 'always', { null: 'ignore' }],
			'no-var': 'error',
			'prefer-const': 'error',
			// SECURITY.md §8: never render anything that did not come from a literal in our source.
			'svelte/no-at-html-tags': 'error',
			// Logging belongs in server code, where it reaches the journal. A component that
			// needs to report something should surface it in the UI.
			'no-console': 'error'
		}
	},
	{
		// Svelte 5 runes require `let` for $props()/$state bindings, so the core rule is a
		// false positive in components; the plugin's version knows about reactive values.
		files: ['**/*.svelte', '**/*.svelte.ts'],
		rules: {
			'prefer-const': 'off',
			'svelte/prefer-const': ['error', { excludeReactiveValues: true }]
		}
	},
	{
		// vigil-web's server half IS the process's logger: console goes to stderr, which
		// systemd captures for the unit.
		files: ['src/lib/server/**', 'src/hooks.server.ts', 'src/routes/**/+*.ts'],
		rules: { 'no-console': 'off' }
	},
	{
		ignores: ['.svelte-kit/', 'build/', 'node_modules/', 'src/lib/components/ui/']
	}
);
