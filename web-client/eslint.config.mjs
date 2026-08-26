import nextVitals from "eslint-config-next/core-web-vitals";
import nextTypescript from "eslint-config-next/typescript";

const eslintConfig = [
  // Build output and deps are not source — flat config has no implicit ignores, so `eslint .`
  // would otherwise lint stale `.next/` artifacts and report thousands of false errors.
  {
    ignores: [".next/**", "node_modules/**", "out/**", "next-env.d.ts"],
  },
  ...nextVitals,
  ...nextTypescript,
  {
    rules: {
      "@typescript-eslint/no-unused-vars": [
        "error",
        { argsIgnorePattern: "^_", varsIgnorePattern: "^_" },
      ],
      "react-hooks/refs": "off",
      "react-hooks/set-state-in-effect": "off",
    },
  },
  {
    files: ["**/reactive-extension/*.ts", "**/reactive-extension/**/*.ts"],
    rules: {
      "react-hooks/rules-of-hooks": "off",
    },
  },
  // UI consumes the src/lib/telemetry/ façade, not the generated wire types (CLAUDE.md). Confining
  // the wire's value fields to the façade is what let #344 (union-typed `value`) and #359
  // (`valueText`/`valueBool` → `state`) each land in `value.ts` alone.
  //
  // Now a blanket ban on the module: #350 moved every UI consumer onto the domain types, so nothing
  // under src/app or src/components has a legitimate reason to name a generated wire type. Scoping
  // it to individual names was only ever a transition measure.
  //
  // `paths` matches the exact specifier, so `.../generated/@types/index` would evade it; the
  // patterns entry closes that. It deliberately does NOT ban `@/lib/infra/aspida-client` itself —
  // one control POST still calls apiClient() directly, and moving it is a separate change.
  {
    files: ["src/app/**", "src/components/**"],
    rules: {
      "no-restricted-imports": [
        "error",
        {
          patterns: [
            {
              group: ["@/lib/infra/aspida-client/generated/**"],
              message:
                "UI consumes the domain façades (src/lib/telemetry/, src/lib/resources/), not the generated wire types. Add the field to the domain type and map it in the façade instead of reaching for the wire shape.",
            },
          ],
        },
      ],
    },
  },
];

export default eslintConfig;
