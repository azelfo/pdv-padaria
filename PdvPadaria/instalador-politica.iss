// CurrentState CBS: https://learn.microsoft.com/en-us/archive/blogs/tip_of_the_day/tip-of-the-day-cbs-servicing-states-chart-refresher
function PacoteSatisfeito(Estado: Cardinal): Boolean;
begin
  Result := (Estado = 80) or (Estado = 112) or (Estado = 128);
end;

// 0 = interromper, 1 = concluído, 2 = reiniciar antes de continuar.
function ResultadoDaAtualizacao(Codigo: Integer; Estado: Cardinal): Integer;
begin
  Result := 0;
  if (Codigo <> 0) and (Codigo <> 3010) then begin
    // Não aplicável só é aceito com prova de instalação anterior/supersedência.
    if (Codigo = -2146498530) and PacoteSatisfeito(Estado) then Result := 1;
    Exit;
  end;
  if (Codigo = 3010) or (Estado = 96) then Result := 2
  else if PacoteSatisfeito(Estado) then Result := 1;
end;
