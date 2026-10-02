"use client";

import {
  listTagSuggestions,
  type TagSuggestion,
} from "@/lib/resources/repository";
import { useEffect, useId, useState, type InputHTMLAttributes } from "react";

const MIN_PREFIX_LENGTH = 2;
const DEBOUNCE_MS = 250;

type Props = Omit<
  InputHTMLAttributes<HTMLInputElement>,
  "value" | "onChange" | "list"
> & {
  value: string;
  onChange: (value: string) => void;
  ariaLabel: string;
  /** Injectable for tests; defaults to the repository façade. */
  suggest?: (prefix: string) => Promise<TagSuggestion[]>;
};

/**
 * A text input that offers existing customTags as completions (#454). Candidates come from
 * `GET /resources/tags` (authorization-scoped, most-used first), so operators reuse `temperature`
 * instead of inventing `temp`. A lookup needs ≥ 2 characters and is debounced; a failed lookup just
 * leaves the input as a plain text field — a tag can always be typed by hand.
 */
export function TagSuggestInput({
  value,
  onChange,
  ariaLabel,
  suggest = listTagSuggestions,
  ...rest
}: Props) {
  const listId = useId();
  const [options, setOptions] = useState<TagSuggestion[]>([]);

  useEffect(() => {
    const prefix = value.trim();
    if (prefix.length < MIN_PREFIX_LENGTH) {
      setOptions([]);
      return;
    }
    let cancelled = false;
    const handle = setTimeout(() => {
      suggest(prefix)
        .then((r) => {
          if (!cancelled) setOptions(r);
        })
        .catch(() => {
          if (!cancelled) setOptions([]);
        });
    }, DEBOUNCE_MS);
    return () => {
      cancelled = true;
      clearTimeout(handle);
    };
  }, [value, suggest]);

  return (
    <>
      <input
        {...rest}
        type="text"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        aria-label={ariaLabel}
        list={listId}
        autoComplete="off"
      />
      <datalist id={listId}>
        {options.map((o) => (
          <option key={o.tag} value={o.tag} label={`${o.tag}（${o.count}）`} />
        ))}
      </datalist>
    </>
  );
}
