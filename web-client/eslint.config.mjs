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
];

export default eslintConfig;
