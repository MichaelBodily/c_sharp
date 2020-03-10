using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Agatha.Common;
using Psi.Business.ServiceContracts.RequestResponse.AdvancePay;
using Psi.Business.ServiceContracts.RequestResponse.AdvancePay.Models;
using Psi.Data.Entities.AdvancePay;
using Psi.Data.Models.Domain.AdvancePay;
using Psi.Utilities.EventAggregator;
using ServiceHost.Features.Events.AdvancePayEvents;

namespace ServiceHost.Features.AdvancePay
{
    // TODO: If this needs to be available for DNA clients we will need to change the suffixes to support long values instead of just ints.
    public class AdvancePayBehaviors
    {
        public const string UnableToLogMessage = "Unable to log the rollover request.";
        public const string IneligibleForRolloverMessage = "The loan for which this rollover request was made is not eligible for rollover at this time.";

        private readonly IPsiRxEventAggregator _eventAggregator;
        private readonly Func<IAdvancePayEntities> _advancePayContextFactory;

        public AdvancePayBehaviors(IPsiRxEventAggregator eventAggregator, Events.EventHandlers eventHandlers, Func<IAdvancePayEntities> advancePayContextFactory)
        {
            _eventAggregator = eventAggregator;
            _advancePayContextFactory = advancePayContextFactory;
            _eventAggregator.GetEvent<AdvancePayRolloverRequestedEvent>().Subscribe(eventHandlers.AdvancePayRolloverRequestedEventHandler);
        }

        public static LoggingCompleteAdvancePayRolloverRequestedEvent LoggingEvent { get; set; }

        public static ReadAdvancePayRolloversResponse GetAdvancePayRolloverInfo(string memberAccountNumber, IAdvancePayEntities context)
        {
            var response = new ReadAdvancePayRolloversResponse { Payload = new List<AdvancePayRollover>() };
            var rolloverRecords = context.Pro_AdvancePay_Rollover.Where(x => x.Acct.ToString() == memberAccountNumber);
            foreach (var record in rolloverRecords)
            {
                var rolloverStatus = GetLoanRolloverStatus(record);

                // If loan is not qualified for rollover, or processing, or the loan record is missing data, do not add it to the response.
                if (!rolloverStatus.HasValue || !record.Sfx.HasValue || !record.LoanFee.HasValue || !record.OrigBal.HasValue)
                {
                    continue;
                }

                var rolloverInfo = new AdvancePayRollover
                {
                    LoanSuffix = record.Sfx.Value,
                    Status = record.Qualify == 1 ? RolloverStatus.Qualified : RolloverStatus.Processing,
                    LoanQualificationDetails = new QualificationDetails
                    {
                        FinanceCharge = record.LoanFee.Value,
                        OriginalLoanAmount = record.OrigBal.Value
                    }
                };
                response.Payload.Add(rolloverInfo);
            }

            return response;
        }

        public static void WriteAdvancePayRolloverRequest(RequestAdvancePayRolloverRequest request, IAdvancePayEntities context)
        {
            var rolloverInfo =
                context.Pro_AdvancePay_Rollover.Single(
                    x => x.Acct.ToString() == request.MemberAccountNumber && x.Sfx == request.LoanAccount);

            var rolloverRequest = new Pro_AdvancePay_RollOver_Action
            {
                Acct = Convert.ToInt32(request.MemberAccountNumber),
                Sfx = request.LoanAccount,
                RespCode = rolloverInfo.RespCode,
                PostResult = "0",
                NewInserted = "Y"
            };

            context.Pro_AdvancePay_RollOver_Action.Add(rolloverRequest);
            context.SaveChanges();
        }

        public static RolloverStatus? GetLoanRolloverStatus(Pro_AdvancePay_Rollover loanInfo)
        {
            if (!loanInfo.Qualify.HasValue)
            {
                return null;
            }

            // The qualification process will not set the value to NULL. Only 0 or 1.  0 is not qualified for the reasons in the ‘Note’ field.
            // For loans that are processing, qualify = 0 and the Note = ‘Rollover’.
            if (loanInfo.Qualify.Value == 0 && !loanInfo.Note.Equals("Rollover", StringComparison.InvariantCultureIgnoreCase))
            {
                return null;
            }

            return loanInfo.Qualify == 1 ? RolloverStatus.Qualified : RolloverStatus.Processing;
        }

        public static RolloverStatus? GetLoanRolloverStatus(string memberAccountNumber, int loanAccountNumber, IAdvancePayEntities context)
        {
            var memberAccountNumberInteger = Convert.ToInt32(memberAccountNumber);
            var loanInfo = context.Pro_AdvancePay_Rollover.Single(x => x.Acct.Value == memberAccountNumberInteger && x.Sfx.Value == loanAccountNumber);
            return GetLoanRolloverStatus(loanInfo);
        }

        public bool IsLoanElligibleForAdvancePayRollover(string memberAccountNumber, string loanAccountNumber)
        {
            if (!int.TryParse(loanAccountNumber, out var suffix) || !int.TryParse(memberAccountNumber, out var account))
            {
                return false;
            }

            using (var context = _advancePayContextFactory())
            {
                var loanInfo = context.Pro_AdvancePay_Rollover.SingleOrDefault(x => x.Acct == account && x.Sfx == suffix);

                if (loanInfo == null)
                {
                    return false;
                }

                var status = GetLoanRolloverStatus(loanInfo);

                return status == RolloverStatus.Qualified;
            }
        }

        public ReadAdvancePayRolloversResponse ReadAdvancePayRollover(ReadAdvancePayRolloversRequest request)
        {
            using (var context = _advancePayContextFactory())
            {
                return GetAdvancePayRolloverInfo(request.MemberAccountNumber, context);
            }
        }

        public RequestAdvancePayRolloverResponse RequestAdvancePayRollover(RequestAdvancePayRolloverRequest request)
        {
            using (var context = _advancePayContextFactory())
            {
                // If the loan is not eligible for rollover, do not submit the request.
                if (GetLoanRolloverStatus(request.MemberAccountNumber, request.LoanAccount, context) != RolloverStatus.Qualified)
                {
                    return new RequestAdvancePayRolloverResponse
                    {
                        SubmitRolloverRequestSuccess = false,
                        ReasonForFailure = IneligibleForRolloverMessage
                    };
                }

                return Task.Factory.StartNew(() => CreateAdvancePayRolloverRequestAsyc(request)).Result;
            }
        }

        /// <summary>
        /// Waits for AdvancePayBehaviors.LoggingEvent to be populated with a LoggingCompleteAdvancePayRolloverRequestedEvent that has a matching guid.  If that event was successful an advance pay rollover
        /// request is written to the Advance Pay table.
        /// </summary>
        public RequestAdvancePayRolloverResponse CreateAdvancePayRolloverRequestAsyc(RequestAdvancePayRolloverRequest request, AdvancePayRolloverRequestedEvent rolloverRequestedEvent)
        {
            var requestLogged = false;
            var response = new RequestAdvancePayRolloverResponse();
            var guid = Guid.Empty;

            // loop until logging event gets filled with a LoggingCompleteAdvancePayRolloverRequestedEvent.
            while (LoggingEvent == null || guid != LoggingEvent.EventGuid)
            {
                if (requestLogged)
                {
                    continue;
                }

                guid = rolloverRequestedEvent.EventGuid;
                _eventAggregator.Publish(rolloverRequestedEvent);
                requestLogged = true;

                Thread.Sleep(10);
            }

            if (LoggingEvent.IsSuccessful)
            {
                try
                {
                    WriteAdvancePayRolloverRequest(request);
                    response.SubmitRolloverRequestSuccess = true;
                    response.ReasonForFailure = string.Empty;
                }
                catch (Exception ex)
                {
                    response.SubmitRolloverRequestSuccess = false;
                    response.ReasonForFailure = ex.Message;
                    response.Exception = new ExceptionInfo(ex);
                    response.ExceptionType = ExceptionType.Business;
                }
            }
            else
            {
                response.SubmitRolloverRequestSuccess = false;
                response.ReasonForFailure = UnableToLogMessage;
                response.Exception = new ExceptionInfo(LoggingEvent.Exception);
                response.ExceptionType = ExceptionType.Business;
            }

            return response;
        }

        /// <summary>
        /// Waits for AdvancePayBehaviors.LoggingEvent to be populated with a LoggingCompleteAdvancePayRolloverRequestedEvent that has a matching guid.  If that event was successful an advance pay rollover
        /// request is written to the Advance Pay table.
        /// </summary>
        public RequestAdvancePayRolloverResponse CreateAdvancePayRolloverRequestAsyc(RequestAdvancePayRolloverRequest request)
        {
            var advancePayRequestedEvent = new AdvancePayRolloverRequestedEvent(request);
            return CreateAdvancePayRolloverRequestAsyc(request, advancePayRequestedEvent);
        }

        public void WriteAdvancePayRolloverRequest(RequestAdvancePayRolloverRequest request)
        {
            using (var context = _advancePayContextFactory())
            {
                WriteAdvancePayRolloverRequest(request, context);
            }
        }

        public ReadAdvancePayEligibilityForAccountResponse ReadAdvancePayEligibilityForAccount(ReadAdvancePayEligibilityForAccountRequest request)
        {
            var userAccount = request.MemberUUID ?? 0;
            var accountWithMaximumLoanAmount = new List<string>();
            var response = new ReadAdvancePayEligibilityForAccountResponse();

            using (var context = _advancePayContextFactory())
            {
                var item = context.Pro_AdvancePay_NewLoan_Eligible.SingleOrDefault(x => x.Account == userAccount);

                if (item?.Account != null)
                {
                    accountWithMaximumLoanAmount.Add(item.Account.ToString());
                }
                else
                {
                    accountWithMaximumLoanAmount.Add(string.Empty);
                }

                if (item?.MaxLoanAmount != null)
                {
                    accountWithMaximumLoanAmount.Add(item.MaxLoanAmount.ToString());
                }
                else
                {
                    accountWithMaximumLoanAmount.Add(string.Empty);
                }
            }

            response.Payload = accountWithMaximumLoanAmount;
            return response;
        }

        public ReadAdvancePayLoanConditionsResponse ReadAdvancePayLoanConditions(ReadAdvancePayLoanConditionsRequest request)
        {
            var response = new ReadAdvancePayLoanConditionsResponse();
            var responseObject = new AdvancePayLoanTermsAndMaxAmountModel();
            var accountNumber = Convert.ToInt64(request.MemberAccountNumber);
            var maxLoanAmt = 0;

            // get the maximumLoanAmount for this account:
            using (var context = _advancePayContextFactory())
            {
                try
                {
                    var item = context.Pro_AdvancePay_NewLoan_Eligible.SingleOrDefault(x => x.Account == accountNumber);
                    if (item != null)
                    {
                        maxLoanAmt = item.MaxLoanAmount ?? 0;
                        if (maxLoanAmt > 0)
                        {
                            maxLoanAmt *= 100;      // Amount is in dollars. convert to cents
                        }
                    }

                    responseObject.MaximumLoanAmount = maxLoanAmt;
                    responseObject.LoanTerms = "success";
                }
                catch (Exception ex)
                {
                    response.Exception = new ExceptionInfo(ex);
                    response.ExceptionType = ExceptionType.Business;
                    responseObject.LoanTerms = "failure";
                }
            }

            response.Payload = responseObject;
            return response;
        }

        public ReadAdvancePayLoanDecisionMessageResponse ReadAdvancePayLoanDecisionMessage(
            ReadAdvancePayLoanDecisionMessageRequest request)
        {
            var response = new ReadAdvancePayLoanDecisionMessageResponse();
            var decisionInfo = new AdvancePayLoanDecisionMessageModel();
            var approvalInfo = new AdvancePayLoanDecisionMessageApprovedModel();
            var deniedInfo = new AdvancePayLoanDecisionMessageDeniedModel();

            var applicant = request.LoanApplicant;
            var insertionMessages = request.InsertionMessages;
            var accountNumber = Convert.ToInt64(request.MemberAccountNumber);
            var requestedLoanAmountInCents = request.LoanAmount * 100;
            var paymentAmountRaw = request.LoanAmount * 1.125 * 100;
            var paymentAmount = unchecked((int)paymentAmountRaw);
            var depositSuffix = request.DepositSuffix;
            var lochmessage = InsertionMessages.FormatLOCHmessage(insertionMessages.LOCHinsertionMessage, accountNumber, requestedLoanAmountInCents, depositSuffix);
            var lomdInsertionMessage = InsertionMessages.FormatLOMDmessage(insertionMessages.LOMDinsertionMessage, accountNumber, requestedLoanAmountInCents, depositSuffix);
            var mmchInsertionMessage = InsertionMessages.FormatMMCHmessage(insertionMessages.MMCHinsertionMessage, accountNumber, applicant);
            var lofeInsertionMessage = InsertionMessages.FormatLOFEmessage(insertionMessages.LOFEinsertionMessage, accountNumber, requestedLoanAmountInCents);
            const string lochTransSourceInsertionMessage = "";
            long identityValue = 0;

            decisionInfo.DecisionMessage = "failure";   // default

            // first stamp the user with the requested loan amount:
            using (var context = _advancePayContextFactory())
            {
                try
                {
                    // Note: NewInserted and TranDate have default values from the database designer.
                    // Set the property 'StoreGeneratedPattern' to 'computed' in the edmx designer.
                    var item = new Pro_TeleTrack_Inquirying()
                    {
                        FName = applicant.FirstName,
                        LName = applicant.LastName,
                        BD = applicant.Birthdate,
                        SSN = applicant.SSN,
                        Add1 = applicant.Address_1,
                        Add2 = applicant.Address_2.Length > 0 ? applicant.Address_2 : string.Empty,
                        City = applicant.City,
                        St = applicant.State,
                        Zip = Convert.ToInt64(applicant.ZipCode),
                        HPhone = applicant.HomePhone,
                        WPhone = applicant.WorkPhone,
                        Employer = applicant.Employer,
                        Acct = accountNumber,
                        Amount = requestedLoanAmountInCents,
                        TransferPymtSource = Convert.ToInt16(depositSuffix),
                        CaseID = "Online Banking",
                        Collateral = 300,
                        PymtAmt = paymentAmount,
                        Email = applicant.Email,
                        Branch = 41,
                        LOCH = lochmessage,
                        LOMD = lomdInsertionMessage,
                        MMCH = mmchInsertionMessage,
                        LOFE = lofeInsertionMessage,
                        LOCH_TransSource = lochTransSourceInsertionMessage
                    };

                    context.Pro_TeleTrack_Inquirying.Add(item);
                    context.SaveChanges();
                    identityValue = item.RecId;
                }
                catch (Exception ex)
                {
                    response.Exception = new ExceptionInfo(ex);
                    response.ExceptionType = ExceptionType.Business;
                }
            }

            if (identityValue > 0)
            {
                for (int i = 0; i < 10; i++)
                {
                    Thread.Sleep(5000); // don't poll for five seconds, continue for 55 seconds
                    using (var context = _advancePayContextFactory())
                    {
                        var loanItem = context.Pro_TeleTrack_Inquirying.FirstOrDefault(x => x.RecId == identityValue);

                        if (loanItem != null && loanItem.NewInserted.ToUpper() == "N")
                        {
                            string maskedAccountNumber = Convert.ToString(accountNumber);
                            maskedAccountNumber = maskedAccountNumber.Substring(maskedAccountNumber.Length - 4).PadLeft(maskedAccountNumber.Length, '*');

                            approvalInfo.LoanInformation = "Account No: " + maskedAccountNumber +
                                                           "; loan Suffix: " + Convert.ToString(loanItem.NewSuffix);
                            approvalInfo.LoanDate = loanItem.TranDate ?? DateTime.MinValue;
                            int loanAmount = loanItem.Amount ?? 0;

                            if (loanAmount > 0)
                            {
                                approvalInfo.LoanAmount = loanAmount;
                                approvalInfo.FinanceCharge = loanAmount * .125;
                            }

                            approvalInfo.DueDate = loanItem.TranDate ?? DateTime.MinValue;
                            approvalInfo.DueDate = approvalInfo.DueDate.AddDays(14);

                            int paymentAmountInDollars = loanItem.PymtAmt ?? 0;

                            if (paymentAmountInDollars > 0)
                            {
                                approvalInfo.PaymentAmount = paymentAmountInDollars;
                            }

                            approvalInfo.AnnualPercentageRate = 325.89;

                            approvalInfo.TransferPaymentSuffix = maskedAccountNumber + "-" + depositSuffix;

                            if (loanItem.OpenCOS == "1")
                            {
                                deniedInfo.HasChargeOff = true;
                            }

                            if (loanItem.SkipGuard == "1")
                            {
                                deniedInfo.HasSkipGuard = true;
                            }

                            if (loanItem.ConsumerDispute == "1")
                            {
                                deniedInfo.HasConsumerDispute = true;
                            }

                            if (loanItem.SocialGuard == "1")
                            {
                                deniedInfo.HasSocialGuard = true;
                            }

                            // load the model with the data:
                            // 0 = Success; 1 = Teletrack Denied; 2 = Summit Failed; 3 = Input Error / Unknown issue
                            if (loanItem.Decision == "0")
                            {
                                decisionInfo.DecisionMessage = "approved"; // should send "approved", "denied", or "failure"
                                decisionInfo.ApprovalInformation = approvalInfo;
                            }
                            else if (loanItem.Decision == "1")
                            {
                                decisionInfo.DecisionMessage = "denied"; // should send "approved", "denied", or "failure"
                                decisionInfo.DeniedInformation = deniedInfo;
                            }
                            else
                            {
                                decisionInfo.DecisionMessage = "failure";
                            }

                            break;
                        }
                    }
                }
            }

            response.Payload = decisionInfo;
            return response;
        }
    }
}
