export interface IDisposable {
  dispose: () => void;
  addTo: (disposables: IDisposable[]) => IDisposable;
  isDisposed: boolean;
  registerDerivative: (disposable: IDisposable) => void;
}

export class Disposable implements IDisposable {
  private readonly disposeImpl: () => void;
  protected readonly derivativeSubjects: IDisposable[] = [];

  public readonly dispose = () => {
    this.disposeImpl();
    this.derivativeSubjects.forEach((x) => x.dispose());
    this.isDisposed = true;
  };

  public readonly addTo: (disposables: IDisposable[]) => IDisposable = (
    disposables,
  ) => {
    disposables.push(this);
    return this;
  };

  public isDisposed: boolean = false;

  public registerDerivative = (disposable: IDisposable) =>
    this.derivativeSubjects.push(disposable);

  public constructor(dispose: () => void) {
    this.disposeImpl = dispose;
  }
}
