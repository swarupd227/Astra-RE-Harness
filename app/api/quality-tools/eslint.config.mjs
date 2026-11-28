// ESLint configuration for the QUALITY gate (Validation/Quality/JsTools.cs). Installed in the API image at
// /opt/astra-jstools beside its node_modules; the gate copies it to the root of each package it reads.
import js from "@eslint/js";
import tseslint from "typescript-eslint";
import reactHooks from "eslint-plugin-react-hooks";
import security from "eslint-plugin-security";
import globals from "globals";

export default [
  { ignores: ["**/node_modules/**", "**/dist/**", "**/build/**", "**/*.min.js", "**/*.d.ts", "eslint.config.mjs"] },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  security.configs.recommended,
  {
    files: ["**/*.{js,jsx,ts,tsx,mjs,cjs}"],
    languageOptions: {
      ecmaVersion: "latest",
      sourceType: "module",
      globals: { ...globals.browser, ...globals.node, ...globals.jest },
      parserOptions: { ecmaFeatures: { jsx: true } },
    },
    plugins: { "react-hooks": reactHooks },
    rules: {
      "react-hooks/rules-of-hooks": "error",
      "react-hooks/exhaustive-deps": "warn",
      "no-eval": "error",
      "no-implied-eval": "error",
      "no-new-func": "error",
      complexity: ["warn", 15],
      // Fires on every obj[key]; too noisy to be a signal in generated form code.
      "security/detect-object-injection": "off",
    },
  },
];
