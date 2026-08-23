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
  // UI consumes the src/lib/telemetry/ façade, not the generated wire types (CLAUDE.md). Keeping the
  // discriminated shape (`valueType`/`valueText`/`valueBool`) inside the façade is what lets the
  // eventual union-typed `value` (#344) land in `value.ts` alone.
  //
  // Scoped to these two names on purpose: `PointDetail` and the other resource types are imported
  // from here too and belong to the separate resources-façade cleanup (#350), so a blanket path ban
  // would misfire. Widen this list when that lands.
  {
    files: ["src/app/**", "src/components/**"],
    rules: {
      "no-restricted-imports": [
        "error",
        {
          paths: [
            {
              name: "@/lib/infra/aspida-client/generated/@types",
              importNames: ["ValidTelemetryData", "LatestSample"],
              message:
                "UI consumes the src/lib/telemetry/ façade (TelemetryPoint / TelemetryLatestSample / TelemetryStatePoint), not the generated wire types.",
            },
          ],
        },
      ],
    },
  },
];

export default eslintConfig;
