/**
 * 画面に表示するための、ユーザ向けのエラー
 */
export class DisplayableError extends Error {
  public static Name = "DisplayableError";
  public constructor(
    public readonly title: string,
    public readonly detail: string,
  ) {
    super(detail);
    this.name = DisplayableError.Name;
  }
}
