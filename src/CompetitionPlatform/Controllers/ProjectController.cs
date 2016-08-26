using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CompetitionPlatform.Data.AzureRepositories.Project;
using CompetitionPlatform.Data.AzureRepositories.Result;
using CompetitionPlatform.Data.AzureRepositories.Users;
using CompetitionPlatform.Data.ProjectCategory;
using CompetitionPlatform.Helpers;
using CompetitionPlatform.Models;
using CompetitionPlatform.Models.ProjectViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace CompetitionPlatform.Controllers
{
    public class ProjectController : Controller
    {
        private readonly IProjectRepository _projectRepository;
        private readonly IProjectCommentsRepository _projectCommentsRepository;
        private readonly IProjectFileRepository _projectFileRepository;
        private readonly IProjectFileInfoRepository _projectFileInfoRepository;
        private readonly IProjectParticipantsRepository _projectParticipantsRepository;
        private readonly IProjectCategoriesRepository _projectCategoriesRepository;
        private readonly IProjectResultRepository _projectResultRepository;
        private readonly IProjectFollowRepository _projectFollowRepository;

        public ProjectController(IProjectRepository projectRepository, IProjectCommentsRepository projectCommentsRepository,
            IProjectFileRepository projectFileRepository, IProjectFileInfoRepository projectFileInfoRepository,
            IProjectParticipantsRepository projectParticipantsRepository, IProjectCategoriesRepository projectCategoriesRepository,
            IProjectResultRepository projectResultRepository, IProjectFollowRepository projectFollowRepository)
        {
            _projectRepository = projectRepository;
            _projectCommentsRepository = projectCommentsRepository;
            _projectFileRepository = projectFileRepository;
            _projectFileInfoRepository = projectFileInfoRepository;
            _projectParticipantsRepository = projectParticipantsRepository;
            _projectCategoriesRepository = projectCategoriesRepository;
            _projectResultRepository = projectResultRepository;
            _projectFollowRepository = projectFollowRepository;
        }

        public IActionResult Create()
        {
            ViewBag.ProjectCategories = _projectCategoriesRepository.GetCategories();
            return View("CreateProject");
        }

        public async Task<IActionResult> Edit(string id)
        {
            var viewModel = await GetProjectViewModel(id);
            return View("EditProject", viewModel);
        }

        [HttpPost]
        public async Task<IActionResult> SaveProject(ProjectViewModel projectViewModel)
        {
            projectViewModel.Tags = TrimAndSerializeTags(projectViewModel.Tags);

            projectViewModel.ProjectStatus = projectViewModel.Status.ToString();

            string projectId;

            if (projectViewModel.Id == null)
            {
                var user = GetAuthenticatedUser();

                projectViewModel.AuthorId = user.Email;
                projectViewModel.AuthorFullName = user.GetFullName();

                projectViewModel.Created = DateTime.UtcNow;
                projectViewModel.ParticipantsCount = 0;

                projectId = await _projectRepository.SaveAsync(projectViewModel);
            }
            else
            {
                projectViewModel.LastModified = DateTime.UtcNow;

                projectId = projectViewModel.Id;

                await _projectRepository.UpdateAsync(projectViewModel);
            }

            await SaveProjectFile(projectViewModel.File, projectId);

            return RedirectToAction("Index", "Home");
        }

        public async Task<IActionResult> ProjectDetails(string id)
        {
            var viewModel = await GetProjectViewModel(id);
            return View(viewModel);
        }

        private async Task<ProjectViewModel> GetProjectViewModel(string id)
        {
            var projectCategories = _projectCategoriesRepository.GetCategories();

            var project = await _projectRepository.GetAsync(id);

            project.Status = (Status)Enum.Parse(typeof(Status), project.ProjectStatus, true);

            var comments = await _projectCommentsRepository.GetProjectCommentsAsync(id);

            var participants = await _projectParticipantsRepository.GetProjectParticipantsAsync(id);

            var results = await _projectResultRepository.GetResultsAsync(id);

            var user = GetAuthenticatedUser();

            var participant = await _projectParticipantsRepository.GetAsync(id, user.Email);

            var participantId = "";
            var isParticipant = false;

            if (participant != null)
            {
                participantId = user.Email;
                isParticipant = true;
            }

            var projectFollowing = await _projectFollowRepository.GetAsync(user.Email, id);
            var isFollowing = projectFollowing != null;

            comments = comments.OrderBy(c => c.Created).Reverse().ToList();

            var statusBarPartial = new ProjectDetailsStatusBarViewModel
            {
                Status = project.Status,
                ParticipantsCount = participants.Count(),
                CompetitionRegistrationDeadline = project.CompetitionRegistrationDeadline,
                ImplementationDeadline = project.ImplementationDeadline,
                VotingDeadline = project.VotingDeadline,
                StatusCompletionPercent = CalculateStatusCompletionPercent(project)
            };

            var commentsPartial = new ProjectCommentPartialViewModel
            {
                ProjectId = project.Id,
                UserId = project.AuthorId,
                Comments = comments
            };

            var participantsPartial = new ProjectParticipantsPartialViewModel
            {
                Participants = participants
            };

            var resultsPartial = new ResultsPartialViewModel
            {
                Status = project.Status,
                Results = results
            };

            var projectViewModel = new ProjectViewModel
            {
                Id = project.Id,
                Name = project.Name,
                Description = project.Description,
                ProjectCategories = projectCategories,
                Category = project.Category,
                Status = project.Status,
                BudgetFirstPlace = project.BudgetFirstPlace,
                BudgetSecondPlace = project.BudgetSecondPlace,
                VotesFor = project.VotesFor,
                VotesAgainst = project.VotesAgainst,
                Created = project.Created,
                LastModified = project.LastModified,
                CompetitionRegistrationDeadline = project.CompetitionRegistrationDeadline,
                ImplementationDeadline = project.ImplementationDeadline,
                VotingDeadline = project.VotingDeadline,
                StatusBarPartial = statusBarPartial,
                CommentsPartial = commentsPartial,
                ParticipantsPartial = participantsPartial,
                ResultsPartial = resultsPartial,
                AuthorId = project.AuthorId,
                AuthorFullName = project.AuthorFullName,
                ParticipantId = participantId,
                IsParticipant = isParticipant,
                IsFollowing = isFollowing,
                OtherProjects = await GetOtherProjects(project.Id)
            };

            if (!string.IsNullOrEmpty(project.Tags))
            {
                projectViewModel.TagsList = JsonConvert.DeserializeObject<List<string>>(project.Tags);

                var builder = new StringBuilder();
                foreach (var tag in projectViewModel.TagsList)
                {
                    builder.Append(tag).Append(", ");
                }
                projectViewModel.Tags = builder.ToString();
            }

            var fileInfo = await _projectFileInfoRepository.GetAsync(id);

            if (fileInfo != null)
            {
                var fileInfoViewModel = new ProjectFileInfoViewModel
                {
                    ContentType = fileInfo.ContentType,
                    FileName = fileInfo.FileName
                };

                projectViewModel.FileInfo = fileInfoViewModel;
            }

            if (projectViewModel.Status == Status.Archive)
                projectViewModel = PopulateResultsViewModel(projectViewModel);

            return projectViewModel;
        }

        private async Task<List<OtherProjectViewModel>> GetOtherProjects(string id)
        {
            var projects = await _projectRepository.GetProjectsAsync();

            var filteredProjects = projects.Where(x => x.Id != id).OrderByDescending(p => p.ParticipantsCount).Take(5).ToList();

            var otherProjects = new List<OtherProjectViewModel>();

            foreach (var project in filteredProjects)
            {
                var otherProject = new OtherProjectViewModel
                {
                    Id = project.Id,
                    Name = project.Name,
                    BudgetFirstPlace = project.BudgetFirstPlace,
                    Members = project.ParticipantsCount
                };

                otherProjects.Add(otherProject);
            }

            return otherProjects;
        }

        private ProjectViewModel PopulateResultsViewModel(ProjectViewModel model)
        {
            model.ResultsPartial.BudgetFirstPlace = model.BudgetFirstPlace;
            model.ResultsPartial.BudgetSecondPlace = model.BudgetSecondPlace;
            model.ResultsPartial.ParticipantCount = model.ParticipantsPartial.Participants.Count();
            model.ResultsPartial.DaysOfContest = (DateTime.UtcNow - model.Created).Days;
            model.ResultsPartial.WinnersCount = 0;

            var firstPlacewinner = model.ResultsPartial.Results.OrderByDescending(x => x.Votes).FirstOrDefault();
            var secondPlacewinners = model.ResultsPartial.Results.OrderByDescending(x => x.Votes).Take(3).Skip(1);

            if (firstPlacewinner.Votes > 0)
            {
                model.ResultsPartial.FirstPlaceWinner = firstPlacewinner;
                model.ResultsPartial.WinnersCount += 1;
            }

            var winnersList = new List<IProjectResultData>();

            if (model.BudgetSecondPlace != null)
            {
                foreach (var winner in secondPlacewinners)
                {
                    if (winner.Votes > 0)
                    {
                        winnersList.Add(winner);
                        model.ResultsPartial.WinnersCount += 1;
                    }
                }
            }

            model.ResultsPartial.SecondPlaceWinners = winnersList;

            return model;
        }

        private string TrimAndSerializeTags(string tagsString)
        {
            if (string.IsNullOrEmpty(tagsString))
                return null;

            var tags = tagsString.Split(",".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
            var tagsList = new List<string>(tags);

            tagsList = tagsList.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            tagsList = tagsList.Select(s => s.Trim()).ToList();

            return JsonConvert.SerializeObject(tagsList);
        }

        private async Task SaveProjectFile(IFormFile file, string projectId)
        {
            if (file != null)
            {
                await _projectFileRepository.InsertProjectFile(file.OpenReadStream(), projectId);

                var fileInfo = new ProjectFileInfoEntity
                {
                    RowKey = projectId,
                    ContentType = file.ContentType,
                    FileName = file.FileName
                };

                await _projectFileInfoRepository.SaveAsync(fileInfo);
            }
        }

        private int CalculateStatusCompletionPercent(IProjectData projectData)
        {
            var completion = 0;

            switch (projectData.Status)
            {
                case Status.Initiative:
                    completion = 100;
                    break;
                case Status.CompetitionRegistration:
                    completion = CalculateDateProgressPercent(projectData.Created,
                        projectData.CompetitionRegistrationDeadline);
                    break;
                case Status.Implementation:
                    completion = CalculateDateProgressPercent(projectData.CompetitionRegistrationDeadline,
                        projectData.ImplementationDeadline);
                    break;
                case Status.Voting:
                    completion = CalculateDateProgressPercent(projectData.ImplementationDeadline,
                        projectData.VotingDeadline);
                    break;
                case Status.Archive:
                    completion = 100;
                    break;
            }
            return completion;
        }

        private int CalculateDateProgressPercent(DateTime start, DateTime end)
        {
            var totalDays = (end - start).Days;

            if (totalDays == 0) return 100;

            var daysPassed = (DateTime.UtcNow - start).Days;
            var percent = daysPassed * 100 / totalDays;

            return (percent > 100) ? 100 : percent;
        }

        private CompetitionPlatformUser GetAuthenticatedUser()
        {
            return ClaimsHelper.GetUser(User.Identity);
        }
    }
}